using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>
/// Maps a Tandem pump-settings snapshot to a Nocturne <see cref="Profile"/> with one store entry per
/// pump profile (IDP). Mirrors <c>tconnectsync</c>'s <c>tandemsource_profile_store</c>: basal and
/// carb ratios are converted from milliunits to units, ISF/correction factors pass through, and the
/// glucose targets come from the pump's CGM alert thresholds.
/// </summary>
public sealed class TandemProfileMapper(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Builds a single profile document, or null when no usable settings are present.</summary>
    public Profile? Map(TandemPumpSettings? settings)
    {
        if (settings?.Profiles?.Profile is not { Count: > 0 } pumpProfiles)
            return null;

        var store = new Dictionary<string, ProfileData>();
        foreach (var pumpProfile in pumpProfiles)
            store[pumpProfile.Name] = BuildProfileData(pumpProfile, settings.CgmSettings);

        if (store.Count == 0)
            return null;

        var active = pumpProfiles.FirstOrDefault(p => p.Idp == settings.Profiles.ActiveIdp);
        var now = DateTime.UtcNow;

        _logger.LogDebug("Mapped Tandem profile with {Count} store entries", store.Count);
        return new Profile
        {
            DefaultProfile = active?.Name ?? pumpProfiles[0].Name,
            Mills = TandemMapHelpers.ToMills(now),
            CreatedAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            StartDate = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            Units = "mg/dl",
            EnteredBy = TandemMapHelpers.Source,
            Store = store,
        };
    }

    private static ProfileData BuildProfileData(TandemPumpProfile profile, TandemPumpCgmSettings? cgm)
    {
        // tconnectsync drops all-zero placeholder segments globally (PumpProfile.__post_init__)
        // before building the profile store, so basal, carb-ratio and ISF all use the same
        // skip-filtered set.
        var segments = profile.TDependentSegs
            .Where(s => !s.Skip)
            .OrderBy(s => s.StartTime)
            .ToList();

        return new ProfileData
        {
            Dia = profile.InsulinDuration / 60.0,
            Timezone = null,
            Units = "mg/dl",
            Basal = segments.Select(s => TimeValue(s.StartTime, s.BasalRate / 1000.0)).ToList(),
            CarbRatio = segments.Select(s => TimeValue(s.StartTime, s.CarbRatio / 1000.0)).ToList(),
            Sens = segments.Select(s => TimeValue(s.StartTime, s.Isf)).ToList(),
            TargetLow = [TimeValue(0, cgm?.LowGlucoseAlert?.MgPerDl ?? 0)],
            TargetHigh = [TimeValue(0, cgm?.HighGlucoseAlert?.MgPerDl ?? 0)],
        };
    }

    private static TimeValue TimeValue(int startMinutes, double value) => new()
    {
        Time = $"{startMinutes / 60:00}:{startMinutes % 60:00}",
        TimeAsSeconds = startMinutes * 60,
        Value = value,
    };
}
