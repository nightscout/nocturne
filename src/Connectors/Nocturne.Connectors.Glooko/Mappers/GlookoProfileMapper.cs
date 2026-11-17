using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Connectors.Core.Utilities;

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

    private IReadOnlyList<T> Records<T>(IEnumerable<T?>? records, string recordType) where T : class =>
        ConnectorFaults.NonNullRecords(records, _logger, _connectorSource, recordType);

    private IEnumerable<(string DeviceGuid, Dictionary<string, GlookoV3PumpSettings> Snapshots)> NonNullDevices(
        Dictionary<string, Dictionary<string, GlookoV3PumpSettings>> pumps)
    {
        foreach (var (deviceGuid, snapshots) in pumps)
        {
            if (snapshots == null)
            {
                _logger.LogInformation(
                    "[{ConnectorSource}] Dropped null settings map for device {DeviceGuid}", _connectorSource, deviceGuid);
                continue;
            }

            yield return (deviceGuid, snapshots);
        }
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

        foreach (var (deviceGuid, settingsSnapshots) in NonNullDevices(response.DeviceSettings.Pumps))
        {
            foreach (var (timestamp, settings) in settingsSnapshots)
            {
                if (settings == null)
                {
                    _logger.LogInformation(
                        "[{ConnectorSource}] Dropped null profile settings for device {DeviceGuid} at {Timestamp}",
                        _connectorSource, deviceGuid, timestamp);
                    continue;
                }

                try
                {
                    var profile = MapSettingsToProfile(settings, timestamp);
                    if (profile != null)
                        profiles.Add(profile);
                }
                catch (Exception ex) when (ConnectorFaults.IsMalformedRecord(ex))
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
    ///     Emits an effective profile activation per device per settings snapshot, derived from
    ///     <c>BasalSettings.ActiveBasalProgram</c>. Consecutive snapshots produce abutting activations;
    ///     the most recent snapshot's is open-ended. Snapshots with a null/empty active program
    ///     are skipped (the next snapshot's timestamp still ends the prior activation).
    /// </summary>
    public List<ProfileActivation> TransformDeviceSettingsToProfileActivations(GlookoV3DeviceSettingsResponse response)
    {
        var activations = new List<ProfileActivation>();

        if (response.DeviceSettings?.Pumps == null)
            return activations;

        foreach (var (deviceGuid, settingsSnapshots) in NonNullDevices(response.DeviceSettings.Pumps))
        {
            var ordered = settingsSnapshots
                .Select(kv => (Key: kv.Key, Parsed: TryParseTimestamp(kv.Key), Settings: kv.Value))
                .Where(x => x.Parsed.HasValue && x.Settings != null)
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
                var startMills = startTimestamp.ToMills();

                activations.Add(new ProfileActivation
                {
                    SyncIdentifier = $"glooko_active_profile_{deviceGuid}_{startMills}",
                    StartTimestamp = startTimestamp,
                    EndTimestamp = endTimestamp,
                    Phase = ProfileActivationPhase.Effective,
                    Origin = ProfileActivationOrigin.Connector,
                    BaseProfileName = profileName,
                    DataSource = _connectorSource,
                });
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} profile activations from device settings",
            _connectorSource,
            activations.Count
        );

        return activations;
    }

    private static DateTime? TryParseTimestamp(string timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
            return null;
        return DateTime.TryParse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
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
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var syncTime))
        {
            _logger.LogWarning("Failed to parse profile timestamp: '{Timestamp}'", timestamp);
            return null;
        }
        var mills = syncTime.ToMills();
        var activeBasalProgram = settings.BasalSettings?.ActiveBasalProgram ?? "Default";

        var store = new Dictionary<string, ProfileData>();

        if (settings.PumpProfilesBasal != null)
            foreach (var basalProfile in Records(settings.PumpProfilesBasal, "basal profile"))
            {
                if (basalProfile.Segments?.Data == null || basalProfile.Segments.Data.Length == 0)
                    continue;

                var profileName = basalProfile.Segments.ProfileName ?? "Default";
                var profileData = GetOrCreateProfileData(store, profileName, settings);
                profileData.Basal = MapSegmentsToTimeValues(Records(basalProfile.Segments.Data, "basal segment"));
            }

        if (settings.ProfilesBolus != null)
            foreach (var bolusProfile in Records(settings.ProfilesBolus, "bolus profile"))
            {
                if (bolusProfile.IsfSegments?.Data is { Length: > 0 })
                {
                    var profileName = bolusProfile.IsfSegments.ProfileName ?? "Default";
                    var profileData = GetOrCreateProfileData(store, profileName, settings);
                    profileData.Sens = MapSegmentsToTimeValues(Records(bolusProfile.IsfSegments.Data, "ISF segment"));
                }

                if (bolusProfile.InsulinToCarbRatioSegments?.Data is { Length: > 0 })
                {
                    var profileName = bolusProfile.InsulinToCarbRatioSegments.ProfileName ?? "Default";
                    var profileData = GetOrCreateProfileData(store, profileName, settings);
                    profileData.CarbRatio = MapSegmentsToTimeValues(Records(bolusProfile.InsulinToCarbRatioSegments.Data, "carb ratio segment"));
                }

                if (bolusProfile.TargetBgSegments?.Data is { Length: > 0 })
                {
                    var profileName = bolusProfile.TargetBgSegments.ProfileName ?? "Default";
                    var profileData = GetOrCreateProfileData(store, profileName, settings);
                    MapTargetBgSegments(profileData, Records(bolusProfile.TargetBgSegments.Data, "target BG segment"));
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

    private static List<TimeValue> MapSegmentsToTimeValues(IReadOnlyList<GlookoV3SegmentData> segments)
    {
        var timeValues = new List<TimeValue>();

        foreach (var segment in segments)
        {
            // Filter out zero-duration segments (sometimes Glooko returns duplicates)
            if (segment.Duration <= 0 && segments.Count > 1)
                continue;

            var tv = HoursToTimeValue(segment.SegmentStart, segment.Value);
            timeValues.Add(tv);
        }

        return timeValues;
    }

    private static void MapTargetBgSegments(ProfileData profileData, IReadOnlyList<GlookoV3TargetBgSegmentData> segments)
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
