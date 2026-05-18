using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Mappers;

public class GlookoProfileMapper
{
    private readonly string _connectorSource;
    private readonly ILogger _logger;

    public GlookoProfileMapper(string connectorSource, ILogger logger)
    {
        _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     Transforms Glooko device settings into Nocturne Profile objects.
    ///     Each historical settings snapshot becomes a separate Profile.
    /// </summary>
    public List<Profile> TransformDeviceSettingsToProfiles(GlookoV3DeviceSettingsResponse response)
    {
        var profiles = new List<Profile>();

        if (response.DeviceSettings?.Pumps == null)
            return profiles;

        foreach (var (deviceGuid, settingsSnapshots) in response.DeviceSettings.Pumps)
        {
            foreach (var (timestamp, settings) in settingsSnapshots)
            {
                try
                {
                    var profile = MapSettingsToProfile(settings, timestamp);
                    if (profile != null)
                        profiles.Add(profile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[{ConnectorSource}] Failed to map profile settings for device {DeviceGuid} at {Timestamp}",
                        _connectorSource,
                        deviceGuid,
                        timestamp
                    );
                }
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} profiles from device settings",
            _connectorSource,
            profiles.Count
        );

        return profiles;
    }

    /// <summary>
    ///     Emits a Profile state_span per device per settings snapshot, derived from
    ///     <c>BasalSettings.ActiveBasalProgram</c>. Consecutive snapshots produce abutting spans;
    ///     the most recent snapshot's span is open-ended. Snapshots with a null/empty active program
    ///     are skipped (the next snapshot's timestamp still closes the prior span).
    /// </summary>
    public List<StateSpan> TransformDeviceSettingsToStateSpans(GlookoV3DeviceSettingsResponse response)
    {
        var stateSpans = new List<StateSpan>();

        if (response.DeviceSettings?.Pumps == null)
            return stateSpans;

        foreach (var (deviceGuid, settingsSnapshots) in response.DeviceSettings.Pumps)
        {
            var ordered = settingsSnapshots
                .Select(kv => (Key: kv.Key, Parsed: TryParseTimestamp(kv.Key), Settings: kv.Value))
                .Where(x => x.Parsed.HasValue)
                .OrderBy(x => x.Parsed!.Value)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var (_, parsed, settings) = ordered[i];
                var profileName = settings.BasalSettings?.ActiveBasalProgram;
                if (string.IsNullOrWhiteSpace(profileName))
                    continue;

                var startTimestamp = parsed!.Value;
                DateTime? endTimestamp = i < ordered.Count - 1 ? ordered[i + 1].Parsed : null;
                var startMills = new DateTimeOffset(startTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

                stateSpans.Add(new StateSpan
                {
                    OriginalId = $"glooko_active_profile_{deviceGuid}_{startMills}",
                    Category = StateSpanCategory.Profile,
                    State = ProfileState.Active.ToString(),
                    StartTimestamp = startTimestamp,
                    EndTimestamp = endTimestamp,
                    Source = _connectorSource,
                    Metadata = new Dictionary<string, object>
                    {
                        { "profileName", profileName }
                    }
                });
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} profile state spans from device settings",
            _connectorSource,
            stateSpans.Count
        );

        return stateSpans;
    }

    private static DateTime? TryParseTimestamp(string timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
            return null;
        return DateTime.TryParse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed) ? parsed : null;
    }

    private Profile? MapSettingsToProfile(GlookoV3PumpSettings settings, string timestamp)
    {
        if (settings.PumpProfilesBasal == null && settings.ProfilesBolus == null)
            return null;

        if (string.IsNullOrWhiteSpace(timestamp))
        {
            _logger.LogWarning("Skipping profile with empty timestamp");
            return null;
        }

        if (!DateTime.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var syncTime))
        {
            _logger.LogWarning("Failed to parse profile timestamp: '{Timestamp}'", timestamp);
            return null;
        }
        var mills = new DateTimeOffset(syncTime).ToUnixTimeMilliseconds();
        var activeBasalProgram = settings.BasalSettings?.ActiveBasalProgram ?? "Default";

        var store = new Dictionary<string, ProfileData>();

        // Map basal profiles
        if (settings.PumpProfilesBasal != null)
            foreach (var basalProfile in settings.PumpProfilesBasal)
            {
                if (basalProfile.Segments?.Data == null || basalProfile.Segments.Data.Length == 0)
                    continue;

                var profileName = basalProfile.Segments.ProfileName ?? "Default";
                var profileData = GetOrCreateProfileData(store, profileName, settings);
                profileData.Basal = MapSegmentsToTimeValues(basalProfile.Segments.Data);
            }

        // Map bolus profiles (ISF, ICR, target BG)
        if (settings.ProfilesBolus != null)
            foreach (var bolusProfile in settings.ProfilesBolus)
            {
                // ISF
                if (bolusProfile.IsfSegments?.Data is { Length: > 0 })
                {
                    var profileName = bolusProfile.IsfSegments.ProfileName ?? "Default";
                    var profileData = GetOrCreateProfileData(store, profileName, settings);
                    profileData.Sens = MapSegmentsToTimeValues(bolusProfile.IsfSegments.Data);
                }

                // ICR
                if (bolusProfile.InsulinToCarbRatioSegments?.Data is { Length: > 0 })
                {
                    var profileName = bolusProfile.InsulinToCarbRatioSegments.ProfileName ?? "Default";
                    var profileData = GetOrCreateProfileData(store, profileName, settings);
                    profileData.CarbRatio = MapSegmentsToTimeValues(bolusProfile.InsulinToCarbRatioSegments.Data);
                }

                // Target BG
                if (bolusProfile.TargetBgSegments?.Data is { Length: > 0 })
                {
                    var profileName = bolusProfile.TargetBgSegments.ProfileName ?? "Default";
                    var profileData = GetOrCreateProfileData(store, profileName, settings);
                    MapTargetBgSegments(profileData, bolusProfile.TargetBgSegments.Data);
                }
            }

        if (store.Count == 0)
            return null;

        // Choose default profile: prefer the active basal program name if it exists in the store
        var defaultProfile = store.ContainsKey(activeBasalProgram)
            ? activeBasalProgram
            : store.Keys.First();

        return new Profile
        {
            Id = $"glooko_{mills}",
            DefaultProfile = defaultProfile,
            StartDate = syncTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Mills = mills,
            CreatedAt = syncTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Units = "mg/dL",
            EnteredBy = "Glooko",
            IsExternallyManaged = true,
            Store = store
        };
    }

    private ProfileData GetOrCreateProfileData(
        Dictionary<string, ProfileData> store,
        string profileName,
        GlookoV3PumpSettings settings)
    {
        if (store.TryGetValue(profileName, out var existing))
            return existing;

        var dia = settings.GeneralSettings?.ActiveInsulinTime ?? 3.0;

        var profileData = new ProfileData
        {
            Dia = dia,
            Units = "mg/dL"
        };

        store[profileName] = profileData;
        return profileData;
    }

    private static List<TimeValue> MapSegmentsToTimeValues(GlookoV3SegmentData[] segments)
    {
        var timeValues = new List<TimeValue>();

        foreach (var segment in segments)
        {
            // Filter out zero-duration segments (sometimes Glooko returns duplicates)
            if (segment.Duration <= 0 && segments.Length > 1)
                continue;

            var tv = HoursToTimeValue(segment.SegmentStart, segment.Value);
            timeValues.Add(tv);
        }

        return timeValues;
    }

    private static void MapTargetBgSegments(ProfileData profileData, GlookoV3TargetBgSegmentData[] segments)
    {
        var targetLow = new List<TimeValue>();
        var targetHigh = new List<TimeValue>();

        foreach (var segment in segments)
        {
            // When valueLow/valueHigh are 0, Glooko uses the single "value" as the target
            var low = segment.ValueLow > 0 ? segment.ValueLow : segment.Value;
            var high = segment.ValueHigh > 0 ? segment.ValueHigh : segment.Value;

            targetLow.Add(HoursToTimeValue(segment.SegmentStart, low));
            targetHigh.Add(HoursToTimeValue(segment.SegmentStart, high));
        }

        profileData.TargetLow = targetLow;
        profileData.TargetHigh = targetHigh;
    }

    /// <summary>
    ///     Converts fractional hours (e.g. 6.5 = 6:30am) to a TimeValue
    /// </summary>
    private static TimeValue HoursToTimeValue(double hours, double value)
    {
        var totalMinutes = (int)Math.Round(hours * 60);
        var h = totalMinutes / 60;
        var m = totalMinutes % 60;
        var timeAsSeconds = totalMinutes * 60;

        return new TimeValue
        {
            Time = $"{h:D2}:{m:D2}",
            Value = value,
            TimeAsSeconds = timeAsSeconds
        };
    }
}
