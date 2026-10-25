using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Treatments;

/// <summary>An override's insulin-needs scale factor over <c>[Start, End)</c>.</summary>
public readonly record struct ScaleFactorInterval(long Start, long End, double Factor);

/// <summary>
/// The scheduled basal a delivered rate is measured against, by net insulin above schedule
/// (<see cref="NetInsulinAboveSchedule"/>) and net basal IOB alike. Under a temp basal that carries a
/// <see cref="TempBasal.ScheduledRate"/> it is that rate, which is what the device measured the temp
/// against. Elsewhere it is the profile's rate, scaled by the override with an insulin-needs scale
/// factor in force; where overrides overlap, the one that started last is in force.
/// </summary>
public sealed class ScheduledBasal
{
    private readonly BasalSegment[] _profile;
    private readonly StepTimeline<double> _factors;

    /// <param name="profile">The profile's scheduled rate, non-overlapping.</param>
    public ScheduledBasal(IEnumerable<BasalSegment> profile, IEnumerable<ScaleFactorInterval> overrides)
    {
        _profile = [.. profile.Where(s => s.EndMills > s.StartMills).OrderBy(s => s.StartMills)];
        _factors = StepTimeline<double>.LatestStartedWins(
            overrides.Select(o => new Step<double>(o.Start, o.End, o.Factor)));
    }

    /// <summary>The rate under <paramref name="temp"/> at <paramref name="mills"/>.</summary>
    public double Under(TempBasal temp, long mills) => temp.ScheduledRate ?? At(mills);

    /// <summary>
    /// The rate at <paramref name="mills"/> where no temp basal reports one. Outside the profile it is
    /// <see cref="TherapySnapshot.DefaultBasalRate"/>, as the profile resolvers fall back to.
    /// </summary>
    public double At(long mills)
    {
        var i = SortedSearch.FirstEndingAfter(_profile, mills, static s => s.EndMills);
        var rate = i < _profile.Length && _profile[i].StartMills <= mills
            ? _profile[i].UnitsPerHour
            : TherapySnapshot.DefaultBasalRate;
        return rate * (_factors.TryAt(mills, out var factor) ? factor : 1);
    }

    /// <summary>
    /// The baseline as steps over the profile: its rate scaled by the overrides, with each of
    /// <paramref name="temps"/> that carries a scheduled rate in its place over the temp's own time.
    /// </summary>
    /// <param name="temps">Non-overlapping, each with an end; see <see cref="NetInsulinAboveSchedule.ClipTempBasals"/>.</param>
    public List<RateSegment> Segments(IEnumerable<TempBasal> temps)
    {
        var steps = _factors.Steps;
        var scaled = new List<RateSegment>();
        foreach (var segment in _profile)
        {
            var cursor = segment.StartMills;
            for (var i = _factors.FirstEndingAfter(cursor); i < steps.Count && steps[i].Start < segment.EndMills; i++)
            {
                if (steps[i].Start > cursor)
                    scaled.Add(new RateSegment(cursor, steps[i].Start, segment.UnitsPerHour));
                var stepEnd = Math.Min(steps[i].End, segment.EndMills);
                scaled.Add(new RateSegment(Math.Max(cursor, steps[i].Start), stepEnd, segment.UnitsPerHour * steps[i].Value));
                cursor = stepEnd;
            }
            if (cursor < segment.EndMills)
                scaled.Add(new RateSegment(cursor, segment.EndMills, segment.UnitsPerHour));
        }

        var reported = temps
            .Where(t => t.ScheduledRate is not null && t.EndMills > t.StartMills)
            .Select(t => new RateSegment(t.StartMills, t.EndMills!.Value, t.ScheduledRate!.Value))
            .ToList();
        if (reported.Count == 0)
            return scaled;

        var result = new List<RateSegment>(reported);
        var covered = IntervalSet.Of(reported.Select(r => new MillsInterval(r.Start, r.End)));
        foreach (var segment in scaled)
            covered.ForEachGap(segment.Start, segment.End, (start, end) => result.Add(new RateSegment(start, end, segment.Rate)));
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>The insulin-needs scale factor of each override span; one still open runs to <paramref name="openEnd"/>.</summary>
    public static IEnumerable<ScaleFactorInterval> Overrides(IEnumerable<StateSpan> overrides, long openEnd) =>
        overrides.Select(o => new ScaleFactorInterval(
            o.StartMills, o.EndMills ?? openEnd, (double)(o.Metadata?.OverrideScaleFactor() ?? 1m)));

    /// <summary>
    /// The override spans overlapping <c>[from, to]</c>, read as
    /// <see cref="IStateSpanRepository.GetByCategories"/> reads them, so an open override that a newer
    /// one superseded before <paramref name="from"/> is not among them.
    /// </summary>
    public static async Task<List<StateSpan>> ReadOverridesAsync(
        IStateSpanRepository stateSpans, long from, long to, CancellationToken ct) =>
        (await stateSpans.GetByCategories(
            [StateSpanCategory.Override],
            DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime,
            DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime,
            ct)).GetValueOrDefault(StateSpanCategory.Override) ?? [];

    /// <summary><see cref="Overrides"/> of <see cref="ReadOverridesAsync"/>.</summary>
    public static async Task<List<ScaleFactorInterval>> OverridesAsync(
        IStateSpanRepository stateSpans, long from, long to, CancellationToken ct) =>
        [.. Overrides(await ReadOverridesAsync(stateSpans, from, to, ct), to)];
}
