using System.Runtime.CompilerServices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;
using Nocturne.API.Services.Caching;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Builds the per-tenant <see cref="ScheduleAssignment"/> timeline for a query window by combining
/// the profile activations, schedule-version history, CCP adjustment, and timezone, then delegates the
/// segment math to the pure <see cref="BasalCalculator"/>.
/// </summary>
/// <remarks>
/// Sub-divides each activation's stretch at every <see cref="BasalSchedule"/> version boundary so
/// mid-window schedule edits are reflected (matching the per-instant behavior of <see cref="IBasalRateResolver"/>).
/// An activation that carries its own schedules is one assignment. Gaps in activation coverage are
/// filled with the default store (<see cref="IActiveProfileResolver.GetDefaultProfileNameAsync"/>), also sub-divided by schedule version. When neither named profile spans nor any default schedule rows exist, an
/// assignment with no entries is emitted: <see cref="BasalCalculator"/> renders that as a constant
/// 1.0 U/hr fallback, identical to <c>BasalRateResolver</c>'s point fallback.
///
/// Per-window assemble cache (5s TTL) in <see cref="ITenantCache"/> absorbs the multi-period dashboard
/// pattern where 1d/3d/7d/30d/90d are requested back-to-back.
/// </remarks>
internal sealed class BasalSegmentService : IBasalSegmentService
{
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly IBasalScheduleRepository _scheduleRepo;
    private readonly ITherapySettingsRepository _therapyRepo;
    private readonly ITenantCache _cache;

    private const int CacheTtlSeconds = 5;

    public BasalSegmentService(
        IActiveProfileResolver activeProfileResolver,
        IBasalScheduleRepository scheduleRepo,
        ITherapySettingsRepository therapyRepo,
        ITenantCache cache)
    {
        _activeProfileResolver = activeProfileResolver;
        _scheduleRepo = scheduleRepo;
        _therapyRepo = therapyRepo;
        _cache = cache;
    }

    public async IAsyncEnumerable<BasalSegment> GetSegmentsAsync(
        long fromMills,
        long toMills,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (toMills <= fromMills)
            yield break;

        var window = new BasalWindow(fromMills, toMills);
        var assignments = await GetAssignmentsAsync(window, ct);

        foreach (var seg in BasalCalculator.BuildSegments(window, assignments))
        {
            ct.ThrowIfCancellationRequested();
            yield return seg;
        }
    }

    private async Task<IReadOnlyList<ScheduleAssignment>> GetAssignmentsAsync(
        BasalWindow window,
        CancellationToken ct)
    {
        return await _cache.GetOrCreateLocalAsync(
            $"basal-assignments:{window.StartMills}:{window.EndMills}",
            () => BuildAssignmentsAsync(window, ct),
            TimeSpan.FromSeconds(CacheTtlSeconds));
    }

    private async Task<IReadOnlyList<ScheduleAssignment>> BuildAssignmentsAsync(
        BasalWindow window,
        CancellationToken ct)
    {
        var toUtc = DateTimeOffset.FromUnixTimeMilliseconds(window.EndMills).UtcDateTime;

        var spans = await _activeProfileResolver.GetActiveProfileSpansForRangeAsync(
            window.StartMills, window.EndMills, ct);

        var defaultProfile = await _activeProfileResolver.GetDefaultProfileNameAsync(ct);
        var spanProfileNames = spans
            .Select(s => s.ProfileName)
            .Distinct()
            .ToList();
        var profileNames = spanProfileNames.Contains(defaultProfile)
            ? spanProfileNames
            : spanProfileNames.Append(defaultProfile).ToList();

        // For each profile name, load schedule-version history and timezone.
        var byProfile = new Dictionary<string, ProfileResources>(StringComparer.Ordinal);
        foreach (var name in profileNames)
        {
            var schedules = (await _scheduleRepo.GetByProfileNameAsync(name, ct))
                .Where(s => s.Timestamp <= toUtc)
                .OrderBy(s => s.Timestamp)
                .ToList();
            var therapy = await _therapyRepo.GetActiveAtAsync(name, toUtc, ct);
            byProfile[name] = new ProfileResources(schedules, therapy?.Timezone);
        }

        var result = new List<ScheduleAssignment>();
        long cursor = window.StartMills;
        foreach (var span in spans)
        {
            var spanStart = Math.Max(Math.Max(span.StartMills, window.StartMills), cursor);
            var spanEnd = Math.Min(span.EndMills ?? window.EndMills, window.EndMills);
            if (spanEnd <= spanStart) continue;

            // Gap before this span → default.
            if (cursor < spanStart)
                result.AddRange(BuildAssignmentsForRange(cursor, spanStart, defaultProfile, byProfile, percentage: 100, timeshiftMs: 0));

            var (percentage, timeshiftMs) = span.Adjustment is { } adjustment
                ? (adjustment.Percentage, adjustment.TimeshiftMs)
                : (100, 0L);
            if (span.Schedules is { } snapshot)
            {
                result.Add(new ScheduleAssignment(
                    spanStart, spanEnd, span.ProfileName, snapshot.Basal ?? [], percentage, timeshiftMs,
                    snapshot.Timezone ?? byProfile.GetValueOrDefault(span.ProfileName)?.TimeZoneId));
            }
            else
            {
                result.AddRange(BuildAssignmentsForRange(spanStart, spanEnd, span.ProfileName, byProfile, percentage, timeshiftMs));
            }

            cursor = spanEnd;
        }

        // Trailing gap → default.
        if (cursor < window.EndMills)
            result.AddRange(BuildAssignmentsForRange(cursor, window.EndMills, defaultProfile, byProfile, percentage: 100, timeshiftMs: 0));

        return result;
    }

    /// <summary>
    /// Sub-divide <c>[fromMills, toMills)</c> by <see cref="BasalSchedule"/> version boundaries for the
    /// given profile, emitting one <see cref="ScheduleAssignment"/> per version slice.
    /// </summary>
    private static IEnumerable<ScheduleAssignment> BuildAssignmentsForRange(
        long fromMills,
        long toMills,
        string profileName,
        Dictionary<string, ProfileResources> byProfile,
        double percentage,
        long timeshiftMs)
    {
        if (toMills <= fromMills) yield break;
        if (!byProfile.TryGetValue(profileName, out var resources))
        {
            // Unknown profile — emit a single empty-entries assignment; calculator falls back to 1.0 U/hr.
            yield return new ScheduleAssignment(fromMills, toMills, profileName, [], percentage, timeshiftMs, null);
            yield break;
        }

        var schedules = resources.Schedules;
        if (schedules.Count == 0)
        {
            yield return new ScheduleAssignment(fromMills, toMills, profileName, [], percentage, timeshiftMs, resources.TimeZoneId);
            yield break;
        }

        // Find the last schedule whose Timestamp ≤ fromMills as the initial active version.
        var fromUtc = DateTimeOffset.FromUnixTimeMilliseconds(fromMills).UtcDateTime;
        var initialIdx = LastIndexAtOrBefore(schedules, fromUtc);
        long sliceStart = fromMills;
        BasalSchedule activeSchedule;

        if (initialIdx >= 0)
        {
            activeSchedule = schedules[initialIdx];
        }
        else
        {
            // No schedule existed at fromMills. Emit a leading slice with no entries up to the
            // first schedule's effective time; the calculator falls back to the default rate
            // there, matching legacy IBasalRateResolver behavior at instants before any schedule.
            var firstStartMs = new DateTimeOffset(schedules[0].Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var leadingEnd = Math.Min(firstStartMs, toMills);
            if (leadingEnd > sliceStart)
            {
                yield return new ScheduleAssignment(
                    sliceStart, leadingEnd, profileName, [], percentage, timeshiftMs, resources.TimeZoneId);
                sliceStart = leadingEnd;
            }
            if (sliceStart >= toMills) yield break;
            activeSchedule = schedules[0];
        }

        // Walk later versions: each one whose Timestamp falls inside (sliceStart, toMills) starts a new slice.
        for (int i = Math.Max(0, initialIdx + 1); i < schedules.Count; i++)
        {
            var versionStartMs = new DateTimeOffset(schedules[i].Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
            if (versionStartMs <= sliceStart) continue;
            if (versionStartMs >= toMills) break;

            yield return new ScheduleAssignment(
                sliceStart, versionStartMs, profileName,
                activeSchedule.Entries, percentage, timeshiftMs, resources.TimeZoneId);

            sliceStart = versionStartMs;
            activeSchedule = schedules[i];
        }

        if (sliceStart < toMills)
            yield return new ScheduleAssignment(
                sliceStart, toMills, profileName,
                activeSchedule.Entries, percentage, timeshiftMs, resources.TimeZoneId);
    }

    private static int LastIndexAtOrBefore(List<BasalSchedule> sortedAsc, DateTime cutoff)
    {
        // Linear scan — typical schedule history is tiny.
        int idx = -1;
        for (int i = 0; i < sortedAsc.Count; i++)
        {
            if (sortedAsc[i].Timestamp <= cutoff) idx = i;
            else break;
        }
        return idx;
    }

    private sealed record ProfileResources(List<BasalSchedule> Schedules, string? TimeZoneId);
}
