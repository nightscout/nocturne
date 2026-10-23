using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>The time each <see cref="LoopAnalysisMasks"/> exclusion can take out of a range.</summary>
public sealed record LoopAnalysisMaskSources
{
    public IReadOnlyList<MillsInterval> Overrides { get; init; } = [];
    public IReadOnlyList<MillsInterval> TemporaryTargets { get; init; } = [];
    public IReadOnlyList<MillsInterval> Exercise { get; init; } = [];
    public IReadOnlyList<MillsInterval> Illness { get; init; } = [];
    public IReadOnlyList<MillsInterval> OpenLoop { get; init; } = [];

    /// <summary><see cref="StateSpanCategory.DataExclusion"/> spans: compression lows, sensor errors.</summary>
    public IReadOnlyList<MillsInterval> DataExclusions { get; init; } = [];

    public IReadOnlyList<ProfileInterval> Profiles { get; init; } = [];
}

public readonly record struct ProfileInterval(string ProfileName, long Start, long End);

/// <summary>
/// The time a range is analysed over once its <see cref="LoopAnalysisMasks"/> are applied, which every
/// Loop Analysis calculator consumes.
/// </summary>
/// <param name="Analysed">The range, less the masked time.</param>
/// <param name="Glucose"><paramref name="Analysed"/> less every data exclusion.</param>
public sealed record LoopAnalysisMask(IntervalSet Analysed, IntervalSet Glucose)
{
    public static LoopAnalysisMask Build(
        LoopAnalysisMasks masks, long from, long to, TimeZoneInfo tz, LoopAnalysisMaskSources sources)
    {
        var analysed = IntervalSet.Of(from, to);

        var days = masks.Days.ToHashSet();
        if (days.Count is > 0 and < 7)
            analysed = analysed.Intersect(LocalWeekdays(from, to, tz, days));

        if (masks.ProfileName is { } profileName)
            analysed = analysed.Intersect(IntervalSet.Of(sources.Profiles
                .Where(p => string.Equals(p.ProfileName, profileName, StringComparison.Ordinal))
                .Select(p => new MillsInterval(p.Start, p.End))));

        var masked = new List<MillsInterval>();
        if (masks.ExcludeOverrides) masked.AddRange(sources.Overrides);
        if (masks.ExcludeTemporaryTargets) masked.AddRange(sources.TemporaryTargets);
        if (masks.ExcludeExercise) masked.AddRange(sources.Exercise);
        if (masks.ExcludeIllness) masked.AddRange(sources.Illness);
        if (masks.ExcludeOpenLoop) masked.AddRange(sources.OpenLoop);
        analysed = analysed.Subtract(IntervalSet.Of(masked));

        return new LoopAnalysisMask(analysed, analysed.Subtract(IntervalSet.Of(sources.DataExclusions)));
    }

    /// <summary>
    /// Time not in closed loop: a pump mode of manual or off, and the time a pump registered with
    /// no AID algorithm was in use, except where AID evidence exists. A registered pump's window is
    /// a span of calendar dates and often outlives the pump's use, so an AID pump's window or an
    /// APS snapshot no older than <see cref="DeviceReportedValues.RecencyThresholdMs"/> wins over it.
    /// </summary>
    public static IntervalSet OpenLoop(
        IEnumerable<MillsInterval> manualPumpModes,
        IEnumerable<MillsInterval> nonAidPumps,
        IEnumerable<MillsInterval> aidPumps,
        IEnumerable<long> apsSnapshotMills)
    {
        var aidEvidence = IntervalSet.Of(aidPumps.Concat(
            apsSnapshotMills.Select(s => new MillsInterval(s, s + DeviceReportedValues.RecencyThresholdMs))));
        return IntervalSet.Of(manualPumpModes).Union(IntervalSet.Of(nonAidPumps).Subtract(aidEvidence));
    }

    private static IntervalSet LocalWeekdays(long from, long to, TimeZoneInfo tz, HashSet<DayOfWeek> days)
    {
        var (firstDay, starts) = LocalSlotBinning.DayStarts(from, to, tz);
        var kept = new List<MillsInterval>();
        for (var d = 0; d < starts.Length - 1; d++)
        {
            if (days.Contains(firstDay.AddDays(d).DayOfWeek))
                kept.Add(new MillsInterval(starts[d], starts[d + 1]));
        }
        return IntervalSet.Of(kept);
    }
}
