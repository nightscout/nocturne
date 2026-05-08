using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Memory;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Builds the per-tenant <see cref="ScheduleAssignment"/> timeline for a query window by combining
/// Profile state-spans, schedule-version history, CCP metadata, and timezone, then delegates the
/// segment math to the pure <see cref="BasalCalculator"/>.
/// </summary>
/// <remarks>
/// Sub-divides each profile-span at every <see cref="BasalSchedule"/> version boundary so mid-window
/// schedule edits are reflected (matching the per-instant behavior of <see cref="IBasalRateResolver"/>).
/// Gaps in profile-span coverage are filled with the implicit "Default" profile, also sub-divided by
/// schedule version. When neither named profile spans nor any default schedule rows exist, an
/// assignment with no entries is emitted: <see cref="BasalCalculator"/> renders that as a constant
/// 1.0 U/hr fallback, identical to <c>BasalRateResolver</c>'s point fallback.
///
/// Per-window assemble cache (5s TTL) keyed by tenant + window absorbs the multi-period dashboard
/// pattern where 1d/3d/7d/30d/90d are requested back-to-back.
/// </remarks>
internal sealed class BasalSegmentService : IBasalSegmentService
{
    private readonly IStateSpanService _stateSpanService;
    private readonly IBasalScheduleRepository _scheduleRepo;
    private readonly ITherapySettingsRepository _therapyRepo;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMemoryCache _cache;

    private const string DefaultProfileName = "Default";
    private const int CacheTtlSeconds = 5;

    public BasalSegmentService(
        IStateSpanService stateSpanService,
        IBasalScheduleRepository scheduleRepo,
        ITherapySettingsRepository therapyRepo,
        ITenantAccessor tenantAccessor,
        IMemoryCache cache)
    {
        _stateSpanService = stateSpanService;
        _scheduleRepo = scheduleRepo;
        _therapyRepo = therapyRepo;
        _tenantAccessor = tenantAccessor;
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
        var cacheKey = $"BasalAssignments:{_tenantAccessor.TenantId}:{window.StartMills}:{window.EndMills}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<ScheduleAssignment>? cached) && cached is not null)
            return cached;

        var assignments = await BuildAssignmentsAsync(window, ct);
        _cache.Set(cacheKey, assignments, TimeSpan.FromSeconds(CacheTtlSeconds));
        return assignments;
    }

    private async Task<IReadOnlyList<ScheduleAssignment>> BuildAssignmentsAsync(
        BasalWindow window,
        CancellationToken ct)
    {
        var fromUtc = DateTimeOffset.FromUnixTimeMilliseconds(window.StartMills).UtcDateTime;
        var toUtc = DateTimeOffset.FromUnixTimeMilliseconds(window.EndMills).UtcDateTime;

        // 1. Profile state-spans overlapping the window.
        var spans = (await _stateSpanService.GetStateSpansAsync(
            category: StateSpanCategory.Profile,
            from: fromUtc,
            to: toUtc,
            count: 1000,
            descending: false,
            cancellationToken: ct))
            .Where(s => s.StartMills < window.EndMills && (!s.EndMills.HasValue || s.EndMills.Value > window.StartMills))
            .OrderBy(s => s.StartMills)
            .ToList();

        // 2. Distinct profile names referenced by spans, plus "Default" for gap-fill.
        var spanProfileNames = spans
            .Select(s => ExtractProfileName(s) ?? DefaultProfileName)
            .Distinct()
            .ToList();
        var profileNames = spanProfileNames.Contains(DefaultProfileName)
            ? spanProfileNames
            : spanProfileNames.Append(DefaultProfileName).ToList();

        // 3. For each profile name, load schedule-version history and timezone.
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

        // 4. Stitch named-profile assignments and gap-filling Default assignments together.
        // Spans may overlap (e.g. concurrent override + base profile); we clamp each span's
        // start to the running cursor so overlap regions don't get emitted twice and the
        // last-starting span wins.
        var result = new List<ScheduleAssignment>();
        long cursor = window.StartMills;
        foreach (var span in spans)
        {
            var spanStart = Math.Max(Math.Max(span.StartMills, window.StartMills), cursor);
            var spanEnd = Math.Min(span.EndMills ?? window.EndMills, window.EndMills);
            if (spanEnd <= spanStart) continue;

            // Gap before this span → default.
            if (cursor < spanStart)
                result.AddRange(BuildAssignmentsForRange(cursor, spanStart, DefaultProfileName, byProfile, percentage: 100, timeshiftMs: 0));

            var profileName = ExtractProfileName(span) ?? DefaultProfileName;
            var (percentage, timeshiftMs) = ExtractCcp(span);
            result.AddRange(BuildAssignmentsForRange(spanStart, spanEnd, profileName, byProfile, percentage, timeshiftMs));

            cursor = spanEnd;
        }

        // Trailing gap → default.
        if (cursor < window.EndMills)
            result.AddRange(BuildAssignmentsForRange(cursor, window.EndMills, DefaultProfileName, byProfile, percentage: 100, timeshiftMs: 0));

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

    private static string? ExtractProfileName(StateSpan span)
    {
        if (span.Metadata is null) return null;
        if (!span.Metadata.TryGetValue("profileName", out var value)) return null;
        return value switch
        {
            null => null,
            string s => s,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je => je.GetString(),
            _ => value.ToString(),
        };
    }

    private static (double Percentage, long TimeshiftMs) ExtractCcp(StateSpan span)
    {
        if (span.Metadata is null) return (100, 0);
        if (!span.Metadata.TryGetValue("percentage", out var pctRaw)) return (100, 0);
        var pct = ToDouble(pctRaw) ?? 100;

        long timeshiftMs = 0;
        if (span.Metadata.TryGetValue("timeshift", out var tsRaw))
        {
            var hours = ToDouble(tsRaw) ?? 0;
            timeshiftMs = (long)(hours % 24 * 3_600_000);
        }
        return (pct, timeshiftMs);
    }

    private static double? ToDouble(object? v) => v switch
    {
        null => null,
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } je => je.GetDouble(),
        _ => double.TryParse(v.ToString(), out var d) ? d : null,
    };

    private sealed record ProfileResources(List<BasalSchedule> Schedules, string? TimeZoneId);
}
