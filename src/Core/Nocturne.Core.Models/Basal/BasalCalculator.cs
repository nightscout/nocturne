namespace Nocturne.Core.Models.Basal;

/// <summary>
/// Pure, deterministic calculations over the basal-rate timeline. No IO, no DI, no async, no clocks.
/// </summary>
/// <remarks>
/// Given a list of <see cref="ScheduleAssignment"/>s covering a query <see cref="BasalWindow"/>,
/// emits a sequence of <see cref="BasalSegment"/>s clipped to the window. Segments are emitted in
/// chronological order. Adjacent segments with identical effective rates are NOT coalesced —
/// callers that care about visual deduplication can post-process.
///
/// Closed-form integration (no time-step sampling): each segment is one schedule entry's tenure
/// inside one assignment's overlap with the window, so total cost is O(assignments × entries × days).
/// For typical inputs (1 assignment, 4 entries, 90 days) that's ~360 segments — bounded and tiny.
/// </remarks>
public static class BasalCalculator
{
    private const double DefaultRate = 1.0;

    /// <summary>
    /// Walk the assignments, emit clipped segments in chronological order. Empty input or an invalid
    /// window yields no segments. Assignments that don't overlap the window are skipped. Assignments
    /// must already be non-overlapping; the orchestrator is responsible for sub-dividing at profile-
    /// switch and schedule-version boundaries.
    /// </summary>
    public static IEnumerable<BasalSegment> BuildSegments(
        BasalWindow window,
        IReadOnlyList<ScheduleAssignment> assignments)
    {
        if (!window.IsValid || assignments.Count == 0)
            yield break;

        var sorted = assignments
            .Where(a => a.EndMills > window.StartMills && a.StartMills < window.EndMills)
            .OrderBy(a => a.StartMills)
            .ToList();

        foreach (var assignment in sorted)
        {
            var aStart = Math.Max(assignment.StartMills, window.StartMills);
            var aEnd = Math.Min(assignment.EndMills, window.EndMills);
            if (aEnd <= aStart) continue;

            foreach (var seg in BuildSegmentsForAssignment(aStart, aEnd, assignment))
                yield return seg;
        }
    }

    /// <summary>
    /// Emit segments for one assignment between <paramref name="fromMills"/> and <paramref name="toMills"/>.
    /// </summary>
    private static IEnumerable<BasalSegment> BuildSegmentsForAssignment(
        long fromMills,
        long toMills,
        ScheduleAssignment assignment)
    {
        if (assignment.Entries.Count == 0)
        {
            // No entries: fall back to default rate (still scaled by CCP).
            var defRate = DefaultRate * (assignment.Percentage / 100.0);
            yield return new BasalSegment(fromMills, toMills, defRate, DefaultRate, assignment.ProfileName);
            yield break;
        }

        var tz = ResolveTimeZone(assignment.TimeZoneId);
        // Dedupe by time-of-day: a duplicate entry (same TimeAsSeconds) would otherwise produce
        // two boundaries at the same instant and silently mask the second value. Keep the last
        // upload for each second-of-day, then sort.
        var entries = assignment.Entries
            .Select(e => (Seconds: e.TimeAsSeconds ?? ParseTimeAsSeconds(e.Time), e.Value))
            .GroupBy(e => e.Seconds)
            .Select(g => g.Last())
            .OrderBy(e => e.Seconds)
            .ToList();

        // Collect boundaries strictly inside (fromMills, toMills), then sort. Sorting matters for
        // negative timeshifts that project a "day N entry" onto day N-1 in absolute time.
        var boundaries = CollectBoundaries(fromMills, toMills, entries, tz, assignment.TimeshiftMs, assignment.Percentage);
        boundaries.Sort((a, b) => a.Mills.CompareTo(b.Mills));

        // Initial rate at fromMills.
        var (rate, scheduled) = ResolveRateAt(fromMills, entries, tz, assignment.TimeshiftMs, assignment.Percentage);
        var segStart = fromMills;

        foreach (var b in boundaries)
        {
            if (b.Mills <= segStart) continue;
            yield return new BasalSegment(segStart, b.Mills, rate, scheduled, assignment.ProfileName);
            segStart = b.Mills;
            rate = b.Effective;
            scheduled = b.Scheduled;
        }

        if (segStart < toMills)
            yield return new BasalSegment(segStart, toMills, rate, scheduled, assignment.ProfileName);
    }

    private readonly record struct Boundary(long Mills, double Effective, double Scheduled);

    private static List<Boundary> CollectBoundaries(
        long fromMills,
        long toMills,
        List<(int Seconds, double Value)> entries,
        TimeZoneInfo tz,
        long timeshiftMs,
        double percentage)
    {
        // Span one local day before fromMills and one after toMills to capture boundaries that
        // straddle midnight after timeshift.
        var fromUtc = DateTimeOffset.FromUnixTimeMilliseconds(fromMills).UtcDateTime;
        var toUtc = DateTimeOffset.FromUnixTimeMilliseconds(toMills).UtcDateTime;
        var firstLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(fromUtc, tz)).AddDays(-1);
        var lastLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(toUtc, tz)).AddDays(1);

        var result = new List<Boundary>(capacity: ((lastLocalDate.DayNumber - firstLocalDate.DayNumber) + 1) * entries.Count);
        for (var day = firstLocalDate; day <= lastLocalDate; day = day.AddDays(1))
        {
            foreach (var entry in entries)
            {
                var localBoundary = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified)
                    .AddSeconds(entry.Seconds);
                var boundaryUtc = LocalToUtcAcrossDst(localBoundary, tz);
                var boundary = new DateTimeOffset(boundaryUtc, TimeSpan.Zero).ToUnixTimeMilliseconds()
                    - timeshiftMs;
                if (boundary <= fromMills) continue;
                if (boundary >= toMills) continue;
                result.Add(new Boundary(boundary, entry.Value * (percentage / 100.0), entry.Value));
            }
        }
        return result;
    }

    private static (double Effective, double Scheduled) ResolveRateAt(
        long atMills,
        List<(int Seconds, double Value)> sortedEntries,
        TimeZoneInfo tz,
        long timeshiftMs,
        double percentage)
    {
        var localTod = LocalSecondsFromMidnight(atMills + timeshiftMs, tz);
        var scheduled = FindEntryAt(sortedEntries, localTod);
        return (scheduled * (percentage / 100.0), scheduled);
    }

    private static double FindEntryAt(List<(int Seconds, double Value)> sortedEntries, int secondsFromMidnight)
    {
        var result = sortedEntries[0].Value;
        foreach (var entry in sortedEntries)
        {
            if (secondsFromMidnight >= entry.Seconds) result = entry.Value;
            else break;
        }
        return result;
    }

    private static int LocalSecondsFromMidnight(long timeMills, TimeZoneInfo tz)
    {
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(timeMills);
        var local = TimeZoneInfo.ConvertTime(dto, tz);
        return (int)local.TimeOfDay.TotalSeconds;
    }

    private static TimeZoneInfo ResolveTimeZone(string? timezoneId)
    {
        if (string.IsNullOrEmpty(timezoneId)) return TimeZoneInfo.Utc;
        return TimeZoneHelper.GetTimeZoneInfoFromId(timezoneId);
    }

    private static int ParseTimeAsSeconds(string hhmm)
    {
        if (TimeOnly.TryParse(hhmm, out var t))
            return (int)t.ToTimeSpan().TotalSeconds;
        return 0;
    }

    /// <summary>
    /// Convert a local DateTime to UTC, handling DST gaps and overlaps explicitly.
    /// Spring-forward (skipped time, e.g. local 02:30 on a US spring-forward day): the schedule
    /// entry is treated as if it activates at the end of the gap (when wall-clocks jump to the
    /// next valid local time), so the new rate kicks in as soon as that local time exists.
    /// Fall-back (ambiguous time, e.g. local 02:30 on a US fall-back day): use the FIRST
    /// occurrence (DST-active interpretation) so the rate transition happens earlier of the two.
    /// </summary>
    private static DateTime LocalToUtcAcrossDst(DateTime localDateTime, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified))
        {
            // Skipped local time. Walk forward minute-by-minute until we land in a valid slot —
            // bounded by the maximum DST gap (typically 1h, never more than 2h in practice).
            for (int i = 1; i <= 180; i++)
            {
                var candidate = unspecified.AddMinutes(i);
                if (!tz.IsInvalidTime(candidate))
                    return TimeZoneInfo.ConvertTimeToUtc(candidate, tz);
            }
            // Pathological tz with a >3h gap — fall back to treating the local time as UTC.
            return DateTime.SpecifyKind(localDateTime, DateTimeKind.Utc);
        }
        if (tz.IsAmbiguousTime(unspecified))
        {
            // Ambiguous: pick the first UTC occurrence. UTC = local - offset, so the EARLIER UTC
            // instant comes from the LARGER signed offset (DST-active interpretation).
            var offsets = tz.GetAmbiguousTimeOffsets(unspecified);
            var firstOccurrenceOffset = offsets.Max();
            return new DateTimeOffset(unspecified, firstOccurrenceOffset).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }
}
