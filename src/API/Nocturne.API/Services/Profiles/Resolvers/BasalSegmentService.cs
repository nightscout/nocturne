using System.Runtime.CompilerServices;
using Nocturne.Core.Clinical.Schedules;
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
/// An activation that carries its own schedules is one assignment. Every other stretch reads the store
/// <see cref="IActiveProfileResolver.GetProfileSpansForRangeAsync"/> names for it, sub-divided by schedule
/// version. When that store has no schedule rows, an assignment with no entries is emitted:
/// <see cref="BasalCalculator"/> renders that as a constant 1.0 U/hr fallback, identical to
/// <c>BasalRateResolver</c>'s point fallback.
///
/// Per-window assemble cache (5s TTL) in <see cref="ITenantCache"/> absorbs the multi-period dashboard
/// pattern where 1d/3d/7d/30d/90d are requested back-to-back.
/// </remarks>
internal sealed class BasalSegmentService : IBasalSegmentService
{
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly IBasalScheduleRepository _scheduleRepo;
    private readonly ITherapySettingsRepository _therapyRepo;
    private readonly IPatientRecordRepository _patientRecordRepo;
    private readonly ITenantCache _cache;

    private const int CacheTtlSeconds = 5;

    public BasalSegmentService(
        IActiveProfileResolver activeProfileResolver,
        IBasalScheduleRepository scheduleRepo,
        ITherapySettingsRepository therapyRepo,
        IPatientRecordRepository patientRecordRepo,
        ITenantCache cache)
    {
        _activeProfileResolver = activeProfileResolver;
        _scheduleRepo = scheduleRepo;
        _therapyRepo = therapyRepo;
        _patientRecordRepo = patientRecordRepo;
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
        var spans = await _activeProfileResolver.GetProfileSpansForRangeAsync(
            window.StartMills, window.EndMills, ct);

        // For each profile name, load schedule and settings version history.
        var patientTimezone = (await _patientRecordRepo.GetAsync(ct))?.Timezone;
        var byProfile = new Dictionary<string, ProfileResources>(StringComparer.Ordinal);
        foreach (var name in spans.Select(s => s.ProfileName).Distinct(StringComparer.Ordinal))
        {
            var schedules = (await _scheduleRepo.GetByProfileNameAsync(name, ct))
                .OrderBy(s => s.Timestamp)
                .ToList();
            var settings = (await _therapyRepo.GetByProfileNameAsync(name, ct))
                .OrderByDescending(t => t.Timestamp)
                .ToArray();
            byProfile[name] = new ProfileResources(schedules, settings, patientTimezone);
        }

        var result = new List<ScheduleAssignment>();
        foreach (var span in spans)
        {
            var (percentage, timeshiftMs) = span.Adjustment is { } adjustment
                ? (adjustment.Percentage, adjustment.TimeshiftMs)
                : (100, 0L);
            var spanEnd = span.EndMills ?? window.EndMills;
            if (span.Schedules is { } snapshot)
            {
                result.Add(new ScheduleAssignment(
                    span.StartMills, spanEnd, span.ProfileName, snapshot.Basal ?? [], percentage, timeshiftMs,
                    byProfile[span.ProfileName].TimeZoneAt(span.StartMills, snapshot.Timezone)));
            }
            else
            {
                result.AddRange(BuildAssignmentsForRange(span.StartMills, spanEnd, span.ProfileName, byProfile, percentage, timeshiftMs));
            }
        }

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
            yield return new ScheduleAssignment(fromMills, toMills, profileName, [], percentage, timeshiftMs, resources.TimeZoneAt(fromMills));
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
            // No schedule existed at fromMills: up to the first one, the newest is read, as
            // ProfileVersionCache.ActiveAt reads it for IBasalRateResolver.
            var firstStartMs = new DateTimeOffset(schedules[0].Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var leadingEnd = Math.Min(firstStartMs, toMills);
            if (leadingEnd > sliceStart)
            {
                yield return new ScheduleAssignment(
                    sliceStart, leadingEnd, profileName, schedules[^1].Entries, percentage, timeshiftMs, resources.TimeZoneAt(sliceStart));
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
                activeSchedule.Entries, percentage, timeshiftMs, resources.TimeZoneAt(sliceStart));

            sliceStart = versionStartMs;
            activeSchedule = schedules[i];
        }

        if (sliceStart < toMills)
            yield return new ScheduleAssignment(
                sliceStart, toMills, profileName,
                activeSchedule.Entries, percentage, timeshiftMs, resources.TimeZoneAt(sliceStart));
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

    /// <param name="Settings">The profile's settings versions, newest first.</param>
    private sealed record ProfileResources(List<BasalSchedule> Schedules, TherapySettings[] Settings, string? PatientTimezone)
    {
        public string? TimeZoneAt(long mills, string? switchTimezone = null) =>
            ScheduleTimeHelper.TimezoneOf(
                PatientTimezone,
                switchTimezone,
                ProfileVersionCache.ActiveAt(Settings, DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime)?.Timezone);
    }
}
