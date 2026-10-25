using System.Buffers;
using System.Text;
using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>
/// One snapshot's prediction curves, mg/dL, indexed by <see cref="PredictionCurve"/>, up to the
/// last horizon; where value <c>i</c> falls is <see cref="PredictionAccuracyCalculator.TargetMills"/>.
/// A curve the snapshot did not carry is null, and so is a value it left out.
/// </summary>
public sealed record PredictionSnapshot(long StartMills, AidAlgorithm Algorithm, double?[]?[] Curves);

/// <summary>Everything the Prediction accuracy page is computed from, fetched once for every range it covers.</summary>
public sealed record PredictionAccuracyInputs
{
    public required TimeZoneInfo Zone { get; init; }

    /// <summary>Ordered by start.</summary>
    public IReadOnlyList<PredictionSnapshot> Snapshots { get; init; } = [];

    /// <summary>
    /// Ordered by time, reaching <see cref="PredictionAccuracyCalculator.ReadingsAfterRangeMs"/>
    /// past the range so a prediction made near its end is paired.
    /// </summary>
    public IReadOnlyList<GlucoseSample> Glucose { get; init; } = [];

    /// <summary>
    /// <see cref="StateSpanCategory.DataExclusion"/> time, to the end of <see cref="Glucose"/>. A
    /// horizon landing in it is counted excluded, neither paired nor unmatched: the readings there
    /// are known to be wrong, not missing.
    /// </summary>
    public IntervalSet GlucoseExclusions { get; init; } = IntervalSet.Empty;

    /// <summary>
    /// The instant the readings are known up to: the end of <see cref="Glucose"/>, or now when that
    /// is earlier. A horizon whose reading could still arrive after it is no prediction at all.
    /// </summary>
    public long ReadingsUntil { get; init; } = long.MaxValue;

    /// <summary>
    /// Carb entries of more than zero grams, ordered, from
    /// <see cref="PredictionAccuracyCalculator.ContextLookbackMs"/> before the range to
    /// <see cref="PredictionAccuracyCalculator.TreatmentsAfterRangeMs"/> after it.
    /// </summary>
    public IReadOnlyList<long> CarbMills { get; init; } = [];

    /// <summary>Manual boluses of more than zero units, ordered, over the same time as <see cref="CarbMills"/>.</summary>
    public IReadOnlyList<long> ManualBolusMills { get; init; } = [];

    /// <summary>Override, temporary target and exercise time.</summary>
    public IntervalSet Adjusted { get; init; } = IntervalSet.Empty;

    /// <summary>
    /// <see cref="LoopAnalysisFilter.HistoryVisibleFrom"/>. A prediction is counted only
    /// <see cref="PredictionAccuracyCalculator.ContextLookbackMs"/> after it, since its context
    /// reads carbs and boluses that far back.
    /// </summary>
    public long? HistoryVisibleFrom { get; init; }
}

/// <summary>
/// Pairs each prediction's value at fixed horizons with the sensor reading nearest the time it
/// predicted, and summarises the error by curve, horizon, local hour of day and context.
/// </summary>
/// <remarks>
/// <para>
/// A prediction counts where its start is in the analysed time, not in a data exclusion, and late
/// enough that its context is visible (<see cref="PredictionAccuracyInputs.HistoryVisibleFrom"/>). Where
/// it does not reach a horizon (the curve is shorter, or leaves the value out), or the reading for
/// the horizon could not have arrived by <see cref="PredictionAccuracyInputs.ReadingsUntil"/>, that
/// horizon is no prediction at all.
/// </para>
/// <para>
/// The page is read for what the settings a prediction ran on got wrong, so a horizon that a carb
/// entry, a manual bolus or the start of an override, temporary target or exercise came before
/// (<see cref="IsInterrupted"/>) is counted interrupted and kept out of every error figure: its
/// reading measures that action, which the prediction could not have known of.
/// </para>
/// </remarks>
public static class PredictionAccuracyCalculator
{
    public const int StepMinutes = 5;

    public static readonly IReadOnlyList<int> HorizonMinutes = [30, 60, 90, 120];

    /// <summary>The horizons whose bias is read as a settings signal; see <see cref="PredictionHorizonAccuracy"/>.</summary>
    public static readonly IReadOnlyList<int> SettingsSignalMinutes = [30, 60];

    public const long MatchToleranceMs = 150_000;

    /// <summary>How far before a prediction carbs or a manual bolus put it in a post-meal or post-correction context.</summary>
    public const long ContextLookbackMs = 3 * LocalSlotBinning.HourMs;

    public static long ReadingsAfterRangeMs => HorizonMinutes[^1] * LocalSlotBinning.MinuteMs + MatchToleranceMs;

    /// <summary>How long after the range a carb entry or bolus can still interrupt a prediction (<see cref="IsInterrupted"/>).</summary>
    public static long TreatmentsAfterRangeMs => HorizonMinutes[^1] * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// The fewest values oref leaves when it drops a level tail from its IOB, COB and UAM curves:
    /// oref0 <c>lib/determine-basal/determine-basal.js:653-690</c> at
    /// <c>88cf032aa74ff25f69464a7d9cd601ee3940c0b3</c> pops trailing equal values while more than 13
    /// remain. Its zero-temp curve is cut where it turns up above target instead, so that tail is not
    /// level and is not held.
    /// </summary>
    private const int OrefTrimmedLength = 13;

    private const long StepMs = StepMinutes * LocalSlotBinning.MinuteMs;

    private static readonly int CurveCount = Enum.GetValues<PredictionCurve>().Length;

    private static readonly int LastIndex = HorizonMinutes[^1] / StepMinutes;

    private static readonly PredictionContext[] AllContexts = Enum.GetValues<PredictionContext>();

    private static readonly PredictionContext[] CarbFree = [PredictionContext.PostCorrection, PredictionContext.Fasting];

    private static readonly PredictionContext[] AfterMeal = [PredictionContext.PostMeal];

    /// <summary>
    /// The curves an algorithm's snapshots can carry, whether or not any in a range did: oref's four
    /// (iAPS is oref), else the one default curve.
    /// </summary>
    public static IReadOnlyList<PredictionCurve> CurvesOf(AidAlgorithm algorithm) =>
        LoopHealthCalculator.IsOref(algorithm)
            ? [PredictionCurve.Iob, PredictionCurve.ZeroTemp, PredictionCurve.Cob, PredictionCurve.Uam]
            : [PredictionCurve.Default];

    /// <summary>Whether a curve is reported in only some contexts, which are drawn from treatments.</summary>
    public static bool IsReportedInSomeContexts(PredictionCurve curve) => ContextsOf(curve).Length < AllContexts.Length;

    /// <summary>See <see cref="PredictionCurveAccuracy.Contexts"/>.</summary>
    private static PredictionContext[] ContextsOf(PredictionCurve curve) => curve switch
    {
        PredictionCurve.Default or PredictionCurve.Uam => AllContexts,
        PredictionCurve.Iob or PredictionCurve.ZeroTemp => CarbFree,
        PredictionCurve.Cob => AfterMeal,
        _ => throw new ArgumentOutOfRangeException(nameof(curve), curve, null),
    };

    /// <summary>
    /// The curves of a stored snapshot. One carrying any of oref's curves is read as oref, whose
    /// default curve the decomposer fills with a copy of the IOB curve (Trio's it leaves empty), so
    /// its default is dropped rather than counted twice.
    /// </summary>
    /// <remarks>
    /// An oref IOB, COB or UAM curve that ends before the last horizon with at least
    /// <see cref="OrefTrimmedLength"/> values had its level tail dropped, so it is read as holding its
    /// last value from there. Any other short curve simply ends.
    /// </remarks>
    /// <returns>Null when the snapshot carries no readable curve.</returns>
    public static PredictionSnapshot? Parse(ApsPredictionPoint point)
    {
        var curves = new double?[]?[CurveCount];
        curves[(int)PredictionCurve.Iob] = ExtendLevelTail(ParseCurve(point.IobJson));
        curves[(int)PredictionCurve.ZeroTemp] = ParseCurve(point.ZtJson);
        curves[(int)PredictionCurve.Cob] = ExtendLevelTail(ParseCurve(point.CobJson));
        curves[(int)PredictionCurve.Uam] = ExtendLevelTail(ParseCurve(point.UamJson));
        if (curves.All(c => c is null))
            curves[(int)PredictionCurve.Default] = ParseCurve(point.DefaultJson);
        if (curves.All(c => c is null))
            return null;

        var start = point.PredictedStart.ToMills();
        return new PredictionSnapshot(start, point.AidAlgorithm, curves);
    }

    /// <summary>
    /// Reads a curve only as far as the last horizon. A 90-day range holds about 26,000 snapshots
    /// of up to four curves each, and deserialising them whole is most of the endpoint's time.
    /// </summary>
    private static double?[]? ParseCurve(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(json.Length));
        try
        {
            var reader = new Utf8JsonReader(bytes.AsSpan(0, Encoding.UTF8.GetBytes(json, bytes)));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                return null;

            var values = new double?[LastIndex + 1];
            var count = 0;
            while (count < values.Length && reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                    return null;
                values[count++] = reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var value) ? value : null;
            }
            return count == 0 ? null : values[..count];
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static double?[]? ExtendLevelTail(double?[]? curve)
    {
        if (curve is null || curve.Length < OrefTrimmedLength || curve.Length > LastIndex || curve[^1] is not { } last)
            return curve;
        var extended = new double?[LastIndex + 1];
        curve.CopyTo(extended, 0);
        Array.Fill(extended, last, curve.Length, extended.Length - curve.Length);
        return extended;
    }

    /// <summary>
    /// When value <paramref name="index"/> of a curve falls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// oref's values are five minutes apart from the start. The start is when the command was
    /// computed (<c>suggested.timestamp</c>), while its value 0 is the reading it ran on, which can
    /// be a few minutes older.
    /// </para>
    /// <para>
    /// Loop's value 0 is at the start, the glucose time, and its later values on the five-minute
    /// grid its effects are computed on: LoopKit <c>LoopKit/LoopMath.swift:162-172</c>,
    /// <c>LoopKit/LoopMath.swift:20-50</c> and <c>LoopKit/Extensions/Date.swift:13-19</c> at
    /// <c>3e828cd22bb24098ec0fff5dd4e208f9c3ae0934</c>. NightscoutService uploads the first value's
    /// date as the start (<c>NightscoutServiceKit/Extensions/StoredDosingDecision.swift:30-35</c> at
    /// <c>2cef4896f0704fb112575ab34b596ee7de78e48d</c>).
    /// </para>
    /// </remarks>
    public static long TargetMills(PredictionSnapshot snapshot, PredictionCurve curve, int index)
    {
        var start = snapshot.StartMills;
        if (index == 0 || curve != PredictionCurve.Default || snapshot.Algorithm != AidAlgorithm.Loop)
            return start + index * StepMs;
        var gridStart = start - ((start % StepMs) + StepMs) % StepMs;
        return gridStart + index * StepMs;
    }

    public static PredictionAccuracyRange Calculate(PredictionAccuracyInputs inputs, LoopAnalysisMask mask, long from, long to)
    {
        var horizons = HorizonMinutes.Count;
        var cells = new Cell[CurveCount][];
        var carried = new bool[CurveCount];
        var algorithms = new SortedSet<AidAlgorithm>();
        var predictions = 0;
        var readableFrom = VisibleHistory.ReadableFrom(inputs.HistoryVisibleFrom, ContextLookbackMs);

        foreach (var snapshot in inputs.Snapshots)
        {
            var start = snapshot.StartMills;
            if (start < from || start >= to || start < readableFrom || !mask.Glucose.Contains(start))
                continue;

            predictions++;
            algorithms.Add(snapshot.Algorithm);
            var local = LocalSlotBinning.LocalTime(start, inputs.Zone);
            var day = DateOnly.FromDateTime(local).DayNumber;
            var context = ContextAt(inputs, start);

            for (var c = 0; c < CurveCount; c++)
            {
                if (snapshot.Curves[c] is not { } curve)
                    continue;
                carried[c] = true;
                var contexts = ContextsOf((PredictionCurve)c);

                for (var h = 0; h < horizons; h++)
                {
                    var index = HorizonMinutes[h] / StepMinutes;
                    if (index >= curve.Length || curve[index] is not { } predicted || !double.IsFinite(predicted))
                        continue;
                    var target = TargetMills(snapshot, (PredictionCurve)c, index);
                    if (target + MatchToleranceMs > inputs.ReadingsUntil)
                        continue;
                    var contextIndex = Array.IndexOf(contexts, context);
                    if (contextIndex < 0)
                        continue;

                    var outcome = inputs.GlucoseExclusions.Contains(target) ? Outcome.Excluded
                        : IsInterrupted(inputs, start, target) ? Outcome.Interrupted
                        : new Outcome(NearestReading(inputs.Glucose, target), Kind.Paired);
                    var curveCells = cells[c] ??= NewCells(contexts.Length, horizons);
                    curveCells[OverallCell].Add(h, predicted, outcome, day);
                    curveCells[HourCell(local.Hour)].Add(h, predicted, outcome, day);
                    curveCells[ContextCell(contextIndex)].Add(h, predicted, outcome, day);
                }
            }
        }

        var result = new PredictionAccuracyRange
        {
            From = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime,
            To = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime,
            Predictions = predictions,
            Algorithms = [.. algorithms],
        };

        for (var c = 0; c < CurveCount; c++)
        {
            if (!carried[c])
                continue;
            var contexts = ContextsOf((PredictionCurve)c);
            var curveCells = cells[c] ?? NewCells(contexts.Length, horizons);
            result.Curves.Add(new PredictionCurveAccuracy
            {
                Curve = (PredictionCurve)c,
                Contexts = [.. contexts],
                Horizons = curveCells[OverallCell].Summarise(),
                ByHour = [.. Enumerable.Range(0, HoursPerDay).Select(hour => new PredictionHourAccuracy
                {
                    Hour = hour,
                    Horizons = curveCells[HourCell(hour)].Summarise(),
                })],
                ByContext = [.. contexts.Select((context, i) => new PredictionContextAccuracy
                {
                    Context = context,
                    Horizons = curveCells[ContextCell(i)].Summarise(),
                })],
            });
        }

        return result;
    }

    /// <summary>The first context of <see cref="PredictionContext"/>'s order that applies at <paramref name="start"/>.</summary>
    public static PredictionContext ContextAt(PredictionAccuracyInputs inputs, long start)
    {
        if (inputs.Adjusted.Contains(start))
            return PredictionContext.Adjusted;
        if (AnyWithin(inputs.CarbMills, start - ContextLookbackMs, start))
            return PredictionContext.PostMeal;
        if (AnyWithin(inputs.ManualBolusMills, start - ContextLookbackMs, start))
            return PredictionContext.PostCorrection;
        return PredictionContext.Fasting;
    }

    /// <summary>
    /// Whether a carb entry, a manual bolus or the start of an adjustment came in
    /// <c>(start, target]</c>. The reading at the target then follows an action the prediction could
    /// not know of, so its error measures the action, not the prediction.
    /// </summary>
    public static bool IsInterrupted(PredictionAccuracyInputs inputs, long start, long target) =>
        AnyWithin(inputs.CarbMills, start + 1, target) || AnyWithin(inputs.ManualBolusMills, start + 1, target)
        || inputs.Adjusted.StartsWithin(start, target);

    private static bool AnyWithin(IReadOnlyList<long> sorted, long from, long to)
    {
        var i = SortedSearch.FirstEndingAfter(sorted, from - 1, static m => m);
        return i < sorted.Count && sorted[i] <= to;
    }

    /// <summary>The reading nearest <paramref name="target"/> within <see cref="MatchToleranceMs"/>; the earlier on a tie.</summary>
    private static double? NearestReading(IReadOnlyList<GlucoseSample> glucose, long target)
    {
        var i = SortedSearch.FirstEndingAfter(glucose, target - MatchToleranceMs - 1, static g => g.Mills);
        double? best = null;
        var bestDistance = long.MaxValue;
        for (; i < glucose.Count && glucose[i].Mills <= target + MatchToleranceMs; i++)
        {
            var distance = Math.Abs(glucose[i].Mills - target);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = glucose[i].Mgdl;
            }
        }
        return best;
    }

    private const int HoursPerDay = 24;
    private const int OverallCell = 0;

    private static int HourCell(int hour) => 1 + hour;

    private static int ContextCell(int index) => 1 + HoursPerDay + index;

    private static Cell[] NewCells(int contexts, int horizons)
    {
        var cells = new Cell[ContextCell(contexts)];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = new Cell(horizons);
        return cells;
    }

    private enum Kind { Paired, Excluded, Interrupted }

    /// <summary>
    /// What a horizon's target found: a reading or none (<see cref="Kind.Paired"/>), excluded time,
    /// or an interrupting action.
    /// </summary>
    private readonly record struct Outcome(double? Actual, Kind Kind)
    {
        public static readonly Outcome Excluded = new(null, Kind.Excluded);
        public static readonly Outcome Interrupted = new(null, Kind.Interrupted);
    }

    private sealed class Cell(int horizons)
    {
        private readonly List<double>[] _errors = [.. Enumerable.Range(0, horizons).Select(_ => new List<double>())];
        private readonly double[] _absolute = new double[horizons];
        private readonly double[] _relative = new double[horizons];
        private readonly int[] _unmatched = new int[horizons];
        private readonly int[] _excluded = new int[horizons];
        private readonly int[] _interrupted = new int[horizons];
        private readonly HashSet<int>[] _days = [.. Enumerable.Range(0, horizons).Select(_ => new HashSet<int>())];

        public void Add(int horizon, double predicted, Outcome outcome, int day)
        {
            if (outcome.Kind == Kind.Excluded)
            {
                _excluded[horizon]++;
                return;
            }
            if (outcome.Kind == Kind.Interrupted)
            {
                _interrupted[horizon]++;
                return;
            }
            if (outcome.Actual is not { } mgdl)
            {
                _unmatched[horizon]++;
                return;
            }

            var error = predicted - mgdl;
            _errors[horizon].Add(error);
            _absolute[horizon] += Math.Abs(error);
            _relative[horizon] += Math.Abs(error) / mgdl;
            _days[horizon].Add(day);
        }

        public List<PredictionHorizonAccuracy> Summarise()
        {
            var result = new List<PredictionHorizonAccuracy>(_errors.Length);
            for (var h = 0; h < _errors.Length; h++)
            {
                var n = _errors[h].Count;
                result.Add(new PredictionHorizonAccuracy
                {
                    Minutes = HorizonMinutes[h],
                    Error = SlotSummary.Of(_errors[h], _days[h].Count),
                    Mae = n == 0 ? null : SlotSummary.Round(_absolute[h] / n),
                    Mard = n == 0 ? null : SlotSummary.Round(100 * _relative[h] / n),
                    Unmatched = _unmatched[h],
                    Excluded = _excluded[h],
                    Interrupted = _interrupted[h],
                    SettingsSignal = SettingsSignalMinutes.Contains(HorizonMinutes[h]),
                });
            }
            return result;
        }
    }
}
