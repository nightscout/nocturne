using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Mappers;

/// <summary>
///     Maps the SSV2 <c>pumps/settings</c> feed to a Nocturne <see cref="Profile"/>. The SSV2-native
///     counterpart to <see cref="GlookoProfileMapper"/> (which maps the v3 <c>devices_and_settings</c>
///     response), producing the same Profile/ProfileData/TimeValue shapes so <c>PublishProfileDataAsync</c>
///     works unchanged. One Profile is produced from the most-current settings snapshot.
///     <para>
///     Unit conventions (see <see cref="GlookoSsv2PumpSettings"/>): segment <c>start</c>/<c>end</c> are
///     seconds-of-day; basal <c>rate</c> is U/hr; carb ratio is g/U; ISF and target-BG are mg/dL × 100
///     (divided by 100 here); <c>active_insulin_time</c> is DIA in seconds (divided by 3600 here).
///     </para>
/// </summary>
public class GlookoSettingsProfileMapper
{
    /// <summary>Glucose values in the SSV2 settings feed are encoded as mg/dL × 100.</summary>
    private const double GlucoseScale = 100.0;

    private readonly string _connectorSource;
    private readonly ILogger _logger;

    public GlookoSettingsProfileMapper(string connectorSource, ILogger logger)
    {
        _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     Transforms a list of SSV2 pump-settings records into a single Nocturne Profile built from the
    ///     most-current snapshot. Soft-deleted records are skipped. Returns null when there is nothing
    ///     mappable (no records, or no basal/bolus programs).
    /// </summary>
    public Profile? TransformSettingsToProfile(IReadOnlyList<GlookoSsv2PumpSettings> records)
    {
        var current = SelectCurrent(records);
        if (current == null)
            return null;

        var profile = MapSettingsToProfile(current);
        if (profile != null)
            _logger.LogInformation(
                "[{ConnectorSource}] Transformed pump settings {Guid} into profile with {Count} program(s)",
                _connectorSource, current.Guid, profile.Store.Count);

        return profile;
    }

    /// <summary>
    ///     Picks the snapshot to map: skips soft-deleted records, prefers the one carrying a current
    ///     (<c>is_current</c>/<c>current</c>) program, and breaks ties (or falls back) on the latest
    ///     <c>pump_timestamp</c>.
    /// </summary>
    private static GlookoSsv2PumpSettings? SelectCurrent(IReadOnlyList<GlookoSsv2PumpSettings>? records)
    {
        if (records == null || records.Count == 0)
            return null;

        return records
            .Where(r => r is { SoftDeleted: false })
            .OrderByDescending(HasCurrentProgram)
            .ThenByDescending(r => ParseTimestamp(r.PumpTimestamp) ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    private static bool HasCurrentProgram(GlookoSsv2PumpSettings settings) =>
        (settings.BasalSettings?.Any(b => b.IsCurrent) ?? false)
        || (settings.BolusSettings?.Any(b => b.Current) ?? false);

    private static DateTime? ParseTimestamp(string? timestamp) =>
        DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private Profile? MapSettingsToProfile(GlookoSsv2PumpSettings settings)
    {
        var store = new Dictionary<string, ProfileData>();
        var dia = settings.ActiveInsulinTime > 0 ? settings.ActiveInsulinTime / 3600.0 : 3.0;

        // Basal programs
        if (settings.BasalSettings != null)
            foreach (var basal in settings.BasalSettings)
            {
                if (basal.Segments is not { Length: > 0 })
                    continue;

                var profileData = GetOrCreateProfileData(store, ResolveName(basal.ProfileName, basal.ProfileId), dia);
                profileData.Basal = basal.Segments
                    .OrderBy(s => s.Start)
                    .Select(s => SecondsToTimeValue(s.Start, s.Rate))
                    .ToList();
            }

        // Bolus programs (ISF, ICR, target BG)
        if (settings.BolusSettings != null)
            foreach (var bolus in settings.BolusSettings)
            {
                var name = ResolveName(bolus.ProfileName, bolus.ProfileId);

                if (bolus.IsfSegments is { Length: > 0 })
                    GetOrCreateProfileData(store, name, dia).Sens = bolus.IsfSegments
                        .OrderBy(s => s.Start)
                        .Select(s => SecondsToTimeValue(s.Start, s.InsulinSensitivityFactor / GlucoseScale))
                        .ToList();

                if (bolus.InsulinToCarbRatioSegments is { Length: > 0 })
                    GetOrCreateProfileData(store, name, dia).CarbRatio = bolus.InsulinToCarbRatioSegments
                        .OrderBy(s => s.Start)
                        .Select(s => SecondsToTimeValue(s.Start, s.InsulinToCarbsRatio))
                        .ToList();

                if (bolus.TargetBgSegments is { Length: > 0 })
                    MapTargetBgSegments(GetOrCreateProfileData(store, name, dia), bolus.TargetBgSegments);
            }

        if (store.Count == 0)
            return null;

        var defaultProfile = ResolveDefaultProfileName(settings, store);
        var timestamp = ParseTimestamp(settings.PumpTimestamp) ?? DateTime.UtcNow;
        var mills = new DateTimeOffset(timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

        // Use the v3 GlookoProfileMapper's exact id scheme (glooko_{mills}, keyed on the snapshot's pump
        // timestamp — both mappers derive mills the same way). This makes the current profile upsert the
        // SAME Profile row whether it came from the v3 devices_and_settings path or the SSV2 pumps/settings
        // path, instead of creating a parallel/duplicate profile when UseSsv2Sync is toggled.
        var id = $"glooko_{mills}";

        return new Profile
        {
            Id = id,
            DefaultProfile = defaultProfile,
            StartDate = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Mills = mills,
            CreatedAt = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Units = "mg/dL",
            EnteredBy = "Glooko",
            IsExternallyManaged = true,
            Store = store
        };
    }

    /// <summary>
    ///     Default profile: the active basal program's name if it exists in the store, else the active bolus
    ///     program's, else the first program.
    /// </summary>
    private static string ResolveDefaultProfileName(GlookoSsv2PumpSettings settings, Dictionary<string, ProfileData> store)
    {
        var activeBasal = settings.BasalSettings?.FirstOrDefault(b => b.IsCurrent);
        if (activeBasal != null)
        {
            var name = ResolveName(activeBasal.ProfileName, activeBasal.ProfileId);
            if (store.ContainsKey(name))
                return name;
        }

        var activeBolus = settings.BolusSettings?.FirstOrDefault(b => b.Current);
        if (activeBolus != null)
        {
            var name = ResolveName(activeBolus.ProfileName, activeBolus.ProfileId);
            if (store.ContainsKey(name))
                return name;
        }

        return store.Keys.First();
    }

    private static string ResolveName(string? profileName, string? profileId) =>
        !string.IsNullOrWhiteSpace(profileName) ? profileName!
        : !string.IsNullOrWhiteSpace(profileId) ? profileId!
        : "Default";

    private static ProfileData GetOrCreateProfileData(Dictionary<string, ProfileData> store, string name, double dia)
    {
        if (store.TryGetValue(name, out var existing))
            return existing;

        var profileData = new ProfileData { Dia = dia, Units = "mg/dL" };
        store[name] = profileData;
        return profileData;
    }

    private static void MapTargetBgSegments(ProfileData profileData, GlookoSsv2TargetBgSegment[] segments)
    {
        var targetLow = new List<TimeValue>();
        var targetHigh = new List<TimeValue>();

        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            // low/high may be null — fall back to the single target_bg for both (matches the v3 mapper's
            // single-value fallback).
            var target = segment.TargetBg ?? 0;
            var low = (segment.TargetBgLow ?? target) / GlucoseScale;
            var high = (segment.TargetBgHigh ?? target) / GlucoseScale;

            targetLow.Add(SecondsToTimeValue(segment.Start, low));
            targetHigh.Add(SecondsToTimeValue(segment.Start, high));
        }

        profileData.TargetLow = targetLow;
        profileData.TargetHigh = targetHigh;
    }

    /// <summary>Converts a seconds-of-day offset (0..86399) plus a value into a TimeValue.</summary>
    private static TimeValue SecondsToTimeValue(int secondsOfDay, double value)
    {
        var totalMinutes = secondsOfDay / 60;
        var h = totalMinutes / 60;
        var m = totalMinutes % 60;

        return new TimeValue
        {
            Time = $"{h:D2}:{m:D2}",
            Value = value,
            TimeAsSeconds = secondsOfDay
        };
    }
}
