using System.Text;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models;

/// <summary>
/// Frozen, request-scoped snapshot of a tenant's therapy state for a single profile segment.
/// Captures DIA, peak, schedules, CCP adjustment, and timezone so per-tick chart-data
/// computations can resolve sensitivity / carb ratio / basal rate via in-memory lookup
/// instead of awaiting four nested async calls per evaluation.
/// </summary>
/// <remarks>
/// <para>
/// CCP scaling direction matches the per-resolver behavior:
/// basal forward (<c>value × pct ÷ 100</c>),
/// sensitivity / carb ratio inverse (<c>value × 100 ÷ pct</c>).
/// </para>
/// <para>
/// Schedule entries are sorted once at construction so each lookup is a forward
/// scan with no allocation. A null entry list means "no schedule loaded for this
/// segment" and the caller-supplied default applies without CCP scaling, matching
/// the early-return branches in the legacy resolvers.
/// </para>
/// <para>
/// CCP is conveyed via primitives (<see cref="CcpPercentage"/> nullable, <see cref="CcpTimeshiftMs"/> long).
/// A null <see cref="CcpPercentage"/> means no CCP active and scaling is skipped — matching the
/// <c>adjustment is null</c> branch in the legacy resolvers.
/// </para>
/// </remarks>
public sealed class TherapySnapshot
{
    public const double DefaultSensitivity = 50.0;
    public const double DefaultCarbRatio = 30.0;
    public const double DefaultBasalRate = 1.0;
    public const double DefaultDia = 3.0;
    public const int DefaultPeakMinutes = 75;
    public const double DefaultCarbsPerHour = 30.0;

    private readonly IReadOnlyList<TimedValue>? _sensitivityEntries;
    private readonly IReadOnlyList<TimedValue>? _carbRatioEntries;
    private readonly IReadOnlyList<TimedValue>? _basalEntries;

    public TherapySnapshot(
        double dia,
        int peakMinutes,
        double carbsPerHour,
        TimeZoneInfo? timezone,
        double? ccpPercentage,
        long ccpTimeshiftMs,
        IEnumerable<ScheduleEntry>? sensitivityEntries,
        IEnumerable<ScheduleEntry>? carbRatioEntries,
        IEnumerable<ScheduleEntry>? basalEntries
    )
    {
        Dia = dia;
        PeakMinutes = peakMinutes;
        CarbsPerHour = carbsPerHour;
        Timezone = timezone;
        CcpPercentage = ccpPercentage;
        CcpTimeshiftMs = ccpTimeshiftMs;
        _sensitivityEntries = Sorted(sensitivityEntries);
        _carbRatioEntries = Sorted(carbRatioEntries);
        _basalEntries = Sorted(basalEntries);
    }

    private readonly record struct TimedValue(int Seconds, double Value);

    private static List<TimedValue>? Sorted(IEnumerable<ScheduleEntry>? entries) =>
        entries?
            .Select(e => new TimedValue(ScheduleEntryTime.SecondsOf(e.TimeAsSeconds, e.Time), e.Value))
            .OrderBy(e => e.Seconds)
            .ToList();

    /// <summary>Duration of Insulin Action (hours).</summary>
    public double Dia { get; }

    /// <summary>Insulin peak time (minutes).</summary>
    public int PeakMinutes { get; }

    /// <summary>Carb absorption rate (g/hr). Profile-level constant; no schedule variation.</summary>
    public double CarbsPerHour { get; }

    /// <summary>Resolved IANA timezone for time-of-day lookup, or <c>null</c> for UTC.</summary>
    public TimeZoneInfo? Timezone { get; }

    /// <summary>CCP percentage (100 = neutral). <c>null</c> means no CCP is active for this segment.</summary>
    public double? CcpPercentage { get; }

    /// <summary>CCP time shift (ms). 0 when no shift, or no CCP.</summary>
    public long CcpTimeshiftMs { get; }

    /// <summary>Returns the active sensitivity (mg/dL per U) at the given time.</summary>
    public double SensitivityAt(long timeMills) =>
        _sensitivityEntries is null
            ? DefaultSensitivity
            : ApplyInverseCcp(LookupAt(_sensitivityEntries, ShiftedSecondsFromMidnight(timeMills)) ?? DefaultSensitivity);

    /// <summary>Returns the active carb ratio (g/U) at the given time.</summary>
    public double CarbRatioAt(long timeMills) =>
        _carbRatioEntries is null
            ? DefaultCarbRatio
            : ApplyInverseCcp(LookupAt(_carbRatioEntries, ShiftedSecondsFromMidnight(timeMills)) ?? DefaultCarbRatio);

    /// <summary>Returns the scheduled basal rate (U/hr) at the given time.</summary>
    public double BasalRateAt(long timeMills) =>
        _basalEntries is null
            ? DefaultBasalRate
            : ApplyForwardCcp(LookupAt(_basalEntries, ShiftedSecondsFromMidnight(timeMills)) ?? DefaultBasalRate);

    /// <summary>
    /// The scheduled basal rate over <c>[fromMills, toMills)</c> as segments, each holding the rate
    /// <see cref="BasalRateAt"/> reads at every instant inside it.
    /// </summary>
    public IEnumerable<BasalSegment> BasalSegments(long fromMills, long toMills) =>
        _basalEntries is null
            ? [new BasalSegment(fromMills, toMills, DefaultBasalRate, DefaultBasalRate, "")]
            : BasalCalculator.BuildSegments(
                new BasalWindow(fromMills, toMills),
                [new ScheduleAssignment(
                    fromMills,
                    toMills,
                    "",
                    [.. _basalEntries.Select(e => new ScheduleEntry { TimeAsSeconds = e.Seconds, Value = e.Value })],
                    CcpPercentage is { } pct && pct > 0 ? pct : 100,
                    CcpTimeshiftMs,
                    Timezone?.Id)]);

    /// <summary>
    /// Appends everything a lookup on this snapshot reads to <paramref name="sb"/>, so two snapshots
    /// that append the same text answer every lookup alike.
    /// </summary>
    public void AppendFingerprint(StringBuilder sb)
    {
        sb.Append(Dia).Append(':')
            .Append(PeakMinutes).Append(':')
            .Append(CarbsPerHour).Append(':')
            .Append(Timezone?.Id ?? "~").Append(':')
            .Append(CcpPercentage?.ToString() ?? "~").Append(':')
            .Append(CcpTimeshiftMs);
        AppendEntries(sb, 's', _sensitivityEntries);
        AppendEntries(sb, 'c', _carbRatioEntries);
        AppendEntries(sb, 'b', _basalEntries);
    }

    private static void AppendEntries(StringBuilder sb, char schedule, IReadOnlyList<TimedValue>? entries)
    {
        sb.Append('|').Append(schedule);
        if (entries is null)
        {
            sb.Append('~');
            return;
        }

        foreach (var entry in entries)
            sb.Append(entry.Seconds).Append('=').Append(entry.Value).Append(',');
    }

    private int ShiftedSecondsFromMidnight(long timeMills) =>
        ScheduleEntryTime.SecondsOfDay(timeMills + CcpTimeshiftMs, Timezone);

    private static double? LookupAt(IReadOnlyList<TimedValue> sortedEntries, int secondsFromMidnight)
    {
        return ScheduleEntryTime.TryEntryAt(sortedEntries, secondsFromMidnight, e => e.Seconds, out var entry)
            ? entry.Value
            : null;
    }

    private double ApplyInverseCcp(double value) =>
        CcpPercentage is { } pct && pct > 0 ? value * 100.0 / pct : value;

    private double ApplyForwardCcp(double value) =>
        CcpPercentage is { } pct && pct > 0 ? value * pct / 100.0 : value;
}

/// <summary>
/// A contiguous time range over which a single <see cref="TherapySnapshot"/> applies.
/// </summary>
public sealed record TherapySegment(long StartMills, long EndMills, TherapySnapshot Snapshot)
{
    public bool Contains(long timeMills) => timeMills >= StartMills && timeMills < EndMills;
}

/// <summary>
/// Ordered list of <see cref="TherapySegment"/> covering a query window without overlap or gap.
/// Lookup cost is amortized O(1) when callers advance monotonically through time
/// (chart-data tick loop) via the sticky cursor.
/// </summary>
public sealed class TherapyTimeline
{
    private readonly IReadOnlyList<TherapySegment> _segments;
    private int _cursor;

    public TherapyTimeline(IReadOnlyList<TherapySegment> segments)
    {
        if (segments.Count == 0)
            throw new ArgumentException("Timeline must contain at least one segment", nameof(segments));
        _segments = segments;
        _cursor = 0;
    }

    public IReadOnlyList<TherapySegment> Segments => _segments;

    /// <summary>The scheduled basal rate over the whole timeline; see <see cref="TherapySnapshot.BasalSegments"/>.</summary>
    public IEnumerable<BasalSegment> BasalSegments() =>
        _segments.SelectMany(s => s.Snapshot.BasalSegments(s.StartMills, s.EndMills));

    /// <summary>
    /// Returns the snapshot active at <paramref name="timeMills"/>.
    /// Advances the internal cursor when the caller queries forward in time.
    /// Falls back to a binary search over the segments if the query is not monotonic.
    /// </summary>
    public TherapySnapshot SnapshotAt(long timeMills)
    {
        // Sticky-cursor fast path
        while (_cursor < _segments.Count - 1 && timeMills >= _segments[_cursor + 1].StartMills)
            _cursor++;

        if (_segments[_cursor].Contains(timeMills))
            return _segments[_cursor].Snapshot;

        // Caller went backward: search, leaving the cursor for the common forward case.
        var lo = 0;
        var hi = _segments.Count - 1;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (_segments[mid].StartMills <= timeMills)
                lo = mid;
            else
                hi = mid - 1;
        }
        if (_segments[lo].Contains(timeMills))
            return _segments[lo].Snapshot;

        // Outside any segment — clamp to nearest. By contract callers query within
        // [first.StartMills, last.EndMills); we still return something sensible.
        return timeMills < _segments[0].StartMills
            ? _segments[0].Snapshot
            : _segments[^1].Snapshot;
    }
}
