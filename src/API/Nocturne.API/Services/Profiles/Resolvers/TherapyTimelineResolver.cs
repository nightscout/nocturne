using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Builds a <see cref="TherapyTimeline"/> by cutting the requested window wherever the stored profile
/// read, the profile activation, or a version of the profile's settings or schedules changes, and
/// eagerly resolving DIA, schedules, timezone, and CCP per segment. Within a segment they are constant,
/// so per-tick chart-data evaluation reduces to in-memory schedule lookup via <see cref="TherapySnapshot"/>.
/// </summary>
internal sealed class TherapyTimelineResolver : ITherapyTimelineResolver
{
    private readonly ITherapySettingsResolver _therapySettings;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ITherapySettingsRepository _therapySettingsRepo;
    private readonly ISensitivityScheduleRepository _sensitivityRepo;
    private readonly ICarbRatioScheduleRepository _carbRatioRepo;
    private readonly IBasalScheduleRepository _basalRepo;
    private readonly ProfileVersionCache _versions;
    private readonly ILogger<TherapyTimelineResolver> _logger;

    public TherapyTimelineResolver(
        ITherapySettingsResolver therapySettings,
        IActiveProfileResolver activeProfileResolver,
        ITherapySettingsRepository therapySettingsRepo,
        ISensitivityScheduleRepository sensitivityRepo,
        ICarbRatioScheduleRepository carbRatioRepo,
        IBasalScheduleRepository basalRepo,
        ProfileVersionCache versions,
        ILogger<TherapyTimelineResolver> logger
    )
    {
        _therapySettings = therapySettings;
        _activeProfileResolver = activeProfileResolver;
        _therapySettingsRepo = therapySettingsRepo;
        _sensitivityRepo = sensitivityRepo;
        _carbRatioRepo = carbRatioRepo;
        _basalRepo = basalRepo;
        _versions = versions;
        _logger = logger;
    }

    public async Task<TherapyTimeline> BuildAsync(
        long fromMills,
        long toMills,
        string? specProfile = null,
        CancellationToken ct = default
    )
    {
        if (toMills <= fromMills)
            throw new ArgumentException("toMills must be greater than fromMills", nameof(toMills));

        // When an explicit profile is supplied, no profile-switch logic applies — single segment.
        if (specProfile is not null)
        {
            var anchor = toMills - 1;
            var snapshot = await ResolveSegmentSnapshotAsync(
                anchor, specProfile, await _activeProfileResolver.GetCircadianAdjustmentAsync(anchor, ct), null, ct);
            return new TherapyTimeline([new TherapySegment(fromMills, toMills, snapshot)]);
        }

        var segments = new List<TherapySegment>();
        foreach (var span in await _activeProfileResolver.GetProfileSpansForRangeAsync(fromMills, toMills, ct))
        {
            var spanEnd = span.EndMills ?? toMills;
            var cuts = span.Schedules is null
                ? await VersionChangesAsync(span.ProfileName, span.StartMills, spanEnd, ct)
                : [];

            var segStart = span.StartMills;
            foreach (var segEnd in cuts.Append(spanEnd))
            {
                var snapshot = await ResolveSegmentSnapshotAsync(
                    segStart, span.ProfileName, span.Adjustment, span.Schedules, ct);
                segments.Add(new TherapySegment(segStart, segEnd, snapshot));
                segStart = segEnd;
            }
        }

        if (segments.Count == 0)
        {
            // Defensive — GetProfileSpansForRangeAsync covers the whole window.
            segments.Add(new TherapySegment(fromMills, toMills, await GetSnapshotAtAsync(toMills - 1, ct: ct)));
        }

        _logger.LogDebug(
            "TherapyTimeline built with {Count} segment(s) for window [{From},{To})",
            segments.Count,
            fromMills,
            toMills
        );

        return new TherapyTimeline(segments);
    }

    public async Task<TherapySnapshot> GetSnapshotAtAsync(
        long timeMills,
        string? specProfile = null,
        CancellationToken ct = default
    )
    {
        var profileName = specProfile ?? await _activeProfileResolver.GetProfileNameAsync(timeMills, ct);
        var schedules = specProfile is null
            ? await _activeProfileResolver.GetActiveSchedulesAsync(timeMills, ct)
            : null;
        return await ResolveSegmentSnapshotAsync(
            timeMills, profileName, await _activeProfileResolver.GetCircadianAdjustmentAsync(timeMills, ct), schedules, ct);
    }

    /// <summary>
    /// The times inside (<paramref name="fromMills"/>, <paramref name="toMills"/>) a version of
    /// <paramref name="profileName"/>'s settings or schedules takes effect, in order.
    /// </summary>
    private async Task<List<long>> VersionChangesAsync(
        string profileName, long fromMills, long toMills, CancellationToken ct)
    {
        // Sequential: see INocturneDbContextFactory.
        List<DateTime> starts =
        [
            .. (await _versions.GetVersionsAsync(_therapySettingsRepo, profileName, ct)).Select(v => v.Timestamp),
            .. (await _versions.GetVersionsAsync(_sensitivityRepo, profileName, ct)).Select(v => v.Timestamp),
            .. (await _versions.GetVersionsAsync(_carbRatioRepo, profileName, ct)).Select(v => v.Timestamp),
            .. (await _versions.GetVersionsAsync(_basalRepo, profileName, ct)).Select(v => v.Timestamp),
        ];

        return starts
            .Select(t => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds())
            .Where(t => t > fromMills && t < toMills)
            .Distinct()
            .Order()
            .ToList();
    }

    /// <param name="ccp">The percentage and timeshift to apply to the schedules.</param>
    /// <param name="schedules">The activation's own schedules, read in place of the stored profile's.</param>
    private async Task<TherapySnapshot> ResolveSegmentSnapshotAsync(
        long anchorMills,
        string profileName,
        CircadianAdjustment? ccp,
        ProfileActivationSnapshot? schedules,
        CancellationToken ct
    )
    {
        var hasData = schedules is not null || await _therapySettings.HasDataAsync(ct);

        if (!hasData)
        {
            return new TherapySnapshot(
                dia: TherapySnapshot.DefaultDia,
                peakMinutes: TherapySnapshot.DefaultPeakMinutes,
                carbsPerHour: TherapySnapshot.DefaultCarbsPerHour,
                timezone: null,
                ccpPercentage: null,
                ccpTimeshiftMs: 0,
                sensitivityEntries: null,
                carbRatioEntries: null,
                basalEntries: null
            );
        }

        var anchorDt = DateTimeOffset.FromUnixTimeMilliseconds(anchorMills).UtcDateTime;

        // Sequential: see INocturneDbContextFactory.
        // An activation's own DIA, carb rate and timezone win over the stored profile's, as
        // TherapySettingsResolver reads them at a point.
        var dia = schedules is not null && TherapySettingsResolver.SnapshotDia(schedules) is { } snapshotDia
            ? snapshotDia
            : await _therapySettings.GetDIAAsync(anchorMills, profileName, ct);
        var carbsHr = schedules is { CarbsHr: > 0 }
            ? schedules.CarbsHr.Value
            : await _therapySettings.GetCarbAbsorptionRateAsync(anchorMills, profileName, ct);
        var timezoneId = schedules is not null
            ? await _therapySettings.GetTimezoneAtAsync(anchorMills, ct: ct)
            : await _therapySettings.GetTimezoneAtAsync(anchorMills, profileName, ct);
        if (schedules is not null)
        {
            return new TherapySnapshot(
                dia: dia,
                peakMinutes: TherapySnapshot.DefaultPeakMinutes,
                carbsPerHour: carbsHr,
                timezone: ResolveTimezone(timezoneId),
                ccpPercentage: ccp?.Percentage,
                ccpTimeshiftMs: ccp?.TimeshiftMs ?? 0,
                sensitivityEntries: schedules.Sensitivity,
                carbRatioEntries: schedules.CarbRatio,
                basalEntries: schedules.Basal
            );
        }

        var sens = await _versions.GetActiveAtAsync(_sensitivityRepo, profileName, anchorDt, ct);
        var carbRatio = await _versions.GetActiveAtAsync(_carbRatioRepo, profileName, anchorDt, ct);
        var basal = await _versions.GetActiveAtAsync(_basalRepo, profileName, anchorDt, ct);

        return new TherapySnapshot(
            dia: dia,
            peakMinutes: TherapySnapshot.DefaultPeakMinutes,
            carbsPerHour: carbsHr,
            timezone: ResolveTimezone(timezoneId),
            ccpPercentage: ccp?.Percentage,
            ccpTimeshiftMs: ccp?.TimeshiftMs ?? 0,
            sensitivityEntries: sens?.Entries,
            carbRatioEntries: carbRatio?.Entries,
            basalEntries: basal?.Entries
        );
    }

    private static TimeZoneInfo? ResolveTimezone(string? id)
        => TimeZoneHelper.TryGetTimeZoneInfoFromId(id, out var tz) ? tz : null;
}
