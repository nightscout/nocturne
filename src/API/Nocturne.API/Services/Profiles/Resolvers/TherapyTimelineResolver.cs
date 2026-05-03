using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Builds a <see cref="TherapyTimeline"/> by enumerating Profile state-span boundaries
/// within the requested window and eagerly resolving DIA, schedules, timezone, and CCP
/// per segment. Within a segment, profile + CCP are constant — so per-tick chart-data
/// evaluation reduces to in-memory schedule lookup via <see cref="TherapySnapshot"/>.
/// </summary>
internal sealed class TherapyTimelineResolver : ITherapyTimelineResolver
{
    private readonly IStateSpanService _stateSpanService;
    private readonly ITherapySettingsResolver _therapySettings;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ISensitivityScheduleRepository _sensitivityRepo;
    private readonly ICarbRatioScheduleRepository _carbRatioRepo;
    private readonly IBasalScheduleRepository _basalRepo;
    private readonly ILogger<TherapyTimelineResolver> _logger;

    public TherapyTimelineResolver(
        IStateSpanService stateSpanService,
        ITherapySettingsResolver therapySettings,
        IActiveProfileResolver activeProfileResolver,
        ISensitivityScheduleRepository sensitivityRepo,
        ICarbRatioScheduleRepository carbRatioRepo,
        IBasalScheduleRepository basalRepo,
        ILogger<TherapyTimelineResolver> logger
    )
    {
        _stateSpanService = stateSpanService;
        _therapySettings = therapySettings;
        _activeProfileResolver = activeProfileResolver;
        _sensitivityRepo = sensitivityRepo;
        _carbRatioRepo = carbRatioRepo;
        _basalRepo = basalRepo;
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
            var snapshot = await ResolveSegmentSnapshotAsync(toMills - 1, specProfile, ct);
            return new TherapyTimeline([new TherapySegment(fromMills, toMills, snapshot)]);
        }

        var boundaries = await DiscoverProfileBoundariesAsync(fromMills, toMills, ct);

        var segments = new List<TherapySegment>(boundaries.Count - 1);
        for (var i = 0; i < boundaries.Count - 1; i++)
        {
            var segStart = boundaries[i];
            var segEnd = boundaries[i + 1];
            // Anchor strictly inside the segment so the resolved profile is the one active here.
            var anchor = segStart;
            var profileName = await _activeProfileResolver.GetActiveProfileNameAsync(anchor, ct);
            var snapshot = await ResolveSegmentSnapshotAsync(anchor, profileName, ct);
            segments.Add(new TherapySegment(segStart, segEnd, snapshot));
        }

        if (segments.Count == 0)
        {
            // Defensive — DiscoverProfileBoundariesAsync always returns at least [fromMills, toMills].
            var snapshot = await ResolveSegmentSnapshotAsync(toMills - 1, null, ct);
            segments.Add(new TherapySegment(fromMills, toMills, snapshot));
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
        var profileName = specProfile
            ?? await _activeProfileResolver.GetActiveProfileNameAsync(timeMills, ct);
        return await ResolveSegmentSnapshotAsync(timeMills, profileName, ct);
    }

    /// <summary>
    /// Returns sorted, distinct segment boundaries spanning [fromMills, toMills].
    /// Includes the window edges plus any Profile state-span StartMills / EndMills that fall
    /// strictly inside the window.
    /// </summary>
    private async Task<List<long>> DiscoverProfileBoundariesAsync(long fromMills, long toMills, CancellationToken ct)
    {
        var fromDt = DateTimeOffset.FromUnixTimeMilliseconds(fromMills).UtcDateTime;
        var toDt = DateTimeOffset.FromUnixTimeMilliseconds(toMills).UtcDateTime;

        // Match ActiveProfileResolver's query shape: get all Profile spans up to the window end,
        // then filter to those overlapping the window. Spans that started before the window are
        // accepted; their StartMills fall before fromMills and contribute no internal boundary.
        var spans = await _stateSpanService.GetStateSpansAsync(
            category: StateSpanCategory.Profile,
            to: toDt,
            count: 1000,
            cancellationToken: ct
        );

        var boundaries = new SortedSet<long> { fromMills, toMills };
        foreach (var span in spans)
        {
            if (span.EndMills.HasValue && span.EndMills.Value <= fromMills)
                continue; // ended before the window
            if (span.StartMills >= toMills)
                continue; // starts after the window

            if (span.StartMills > fromMills && span.StartMills < toMills)
                boundaries.Add(span.StartMills);
            if (span.EndMills is { } endMills && endMills > fromMills && endMills < toMills)
                boundaries.Add(endMills);
        }

        return boundaries.ToList();
    }

    private async Task<TherapySnapshot> ResolveSegmentSnapshotAsync(
        long anchorMills,
        string? profileName,
        CancellationToken ct
    )
    {
        var hasData = await _therapySettings.HasDataAsync(ct);

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

        var resolvedProfile = profileName ?? "Default";
        var anchorDt = DateTimeOffset.FromUnixTimeMilliseconds(anchorMills).UtcDateTime;

        // Resolve sequentially: each repo here shares a single scoped DbContext, so parallel
        // awaits would trigger "A second operation was started on this context instance".
        var dia = await _therapySettings.GetDIAAsync(anchorMills, resolvedProfile, ct);
        var carbsHr = await _therapySettings.GetCarbAbsorptionRateAsync(anchorMills, resolvedProfile, ct);
        var timezoneId = await _therapySettings.GetTimezoneAsync(resolvedProfile, ct);
        var sens = await _sensitivityRepo.GetActiveAtAsync(resolvedProfile, anchorDt, ct);
        var carbRatio = await _carbRatioRepo.GetActiveAtAsync(resolvedProfile, anchorDt, ct);
        var basal = await _basalRepo.GetActiveAtAsync(resolvedProfile, anchorDt, ct);
        var ccp = await _activeProfileResolver.GetCircadianAdjustmentAsync(anchorMills, ct);

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
    {
        if (string.IsNullOrEmpty(id))
            return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
