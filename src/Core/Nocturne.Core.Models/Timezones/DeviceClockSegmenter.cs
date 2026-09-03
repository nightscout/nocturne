namespace Nocturne.Core.Models.Timezones;

/// <summary>
/// Derives fixed-offset <see cref="DeviceClockSegment"/>s from a connector's ordered
/// <see cref="DeviceClockObservation"/> evidence. Segmentation is a pure function of the
/// observations, so re-derivation is idempotent and never accumulates state of its own.
///
/// Gates, in the order they protect the data:
/// <list type="bullet">
/// <item>A deviation under <see cref="MinDeviationMinutes"/> is device clock drift, not a timezone —
/// it never produces a segment.</item>
/// <item>A single anomalous observation can never move anything: a segment needs at least
/// <see cref="MinConsecutiveObservations"/> consecutive supporting observations.</item>
/// <item>Estimates must agree within <see cref="AgreementToleranceMinutes"/>; when consecutive
/// estimates destabilise (erratic uploads), the run breaks and the segmenter stays silent.</item>
/// <item>Lower bounds are one-sided: a high bound proves the clock ran ahead, but a low bound is
/// neutral — compatible with both states — so it neither supports nor refutes a run. Only an
/// estimate near the expected offset refutes one.</item>
/// </list>
/// </summary>
public static class DeviceClockSegmenter
{
    /// <summary>Deviations below this are drift, not a timezone; they never form a segment.</summary>
    public const int MinDeviationMinutes = 30;

    /// <summary>Minimum consecutive supporting observations before a segment exists.</summary>
    public const int MinConsecutiveObservations = 3;

    /// <summary>How closely two-sided estimates in one run must agree with each other.</summary>
    public const int AgreementToleranceMinutes = 20;

    /// <summary>
    /// Maximum time between consecutive supporting observations; a longer silence splits the run so
    /// two separate trips cannot be bridged by sparse evidence.
    /// </summary>
    public const int MaxGapHours = 48;

    /// <summary>Real timezone offsets fall on this granularity; derived offsets snap to it.</summary>
    public const int OffsetGranularityMinutes = 15;

    /// <summary>
    /// Derives segments from observations of a single connector.
    /// </summary>
    /// <param name="observationsAscending">The connector's observations ordered by <see cref="DeviceClockObservation.ObservedAtUtc"/>.</param>
    /// <param name="expectedOffsetMinutesAtUtc">
    /// The offset (minutes east of UTC) the tenant's timezone timeline predicts at a real-UTC instant
    /// — DST-aware, so a device correctly following its zone never deviates.
    /// </param>
    public static IReadOnlyList<DeviceClockSegment> Derive(
        IReadOnlyList<DeviceClockObservation> observationsAscending,
        Func<DateTime, int> expectedOffsetMinutesAtUtc)
    {
        var segments = new List<DeviceClockSegment>();
        var run = new List<DeviceClockObservation>();
        // The most recent evidence seen outside the current run. Floors the segment start so a
        // backlog flush (whose oldest records predate the deviation) cannot stretch it backwards,
        // and ends a segment at its last supporting evidence when later evidence closes it.
        DateTime? preRunFloor = null;

        foreach (var obs in observationsAscending)
        {
            var deviation = obs.OffsetMinutes - expectedOffsetMinutesAtUtc(obs.ObservedAtUtc);
            var deviant = obs.IsEstimate
                ? Math.Abs(deviation) >= MinDeviationMinutes
                : deviation >= MinDeviationMinutes;
            var refuting = obs.IsEstimate && !deviant;

            if (run.Count > 0)
            {
                var gapExceeded = (obs.ObservedAtUtc - run[^1].ObservedAtUtc).TotalHours > MaxGapHours;
                var incompatible = deviant && obs.IsEstimate && !AgreesWithRunEstimates(run, obs);

                if (refuting || gapExceeded || incompatible)
                {
                    FinishRun(segments, run, preRunFloor, closedByLaterEvidence: true);
                    preRunFloor = run[^1].ObservedAtUtc;
                    run.Clear();
                }
            }

            if (deviant)
                run.Add(obs);
            else if (run.Count == 0)
                preRunFloor = obs.ObservedAtUtc;
            // A neutral bound inside a run (low value, unknown lag) neither supports nor refutes it.
        }

        if (run.Count > 0)
            FinishRun(segments, run, preRunFloor, closedByLaterEvidence: false);

        return segments;
    }

    private static bool AgreesWithRunEstimates(List<DeviceClockObservation> run, DeviceClockObservation candidate)
    {
        foreach (var existing in run)
        {
            if (existing.IsEstimate
                && Math.Abs(existing.OffsetMinutes - candidate.OffsetMinutes) > AgreementToleranceMinutes)
                return false;
        }

        return true;
    }

    private static void FinishRun(
        List<DeviceClockSegment> segments,
        List<DeviceClockObservation> run,
        DateTime? preRunFloor,
        bool closedByLaterEvidence)
    {
        if (run.Count < MinConsecutiveObservations)
            return;

        var from = run.Min(o => o.CoversFromUtc ?? o.ObservedAtUtc);
        if (preRunFloor is { } floor && from < floor)
            from = floor;

        var estimates = run.Where(o => o.IsEstimate).Select(o => o.OffsetMinutes).OrderBy(v => v).ToList();
        // Estimates carry the true value, so their median wins; a bound-only run corrects with its
        // tightest (largest) bound — still conservative, since every bound is a floor on the offset.
        var offset = SnapToGranularity(estimates.Count > 0 ? Median(estimates) : run.Max(o => o.OffsetMinutes));

        // The deviation is only evidenced up to the last supporting observation; when something later
        // closed the run, end there rather than at the closing evidence (the gap between is unknown).
        var to = closedByLaterEvidence ? run[^1].ObservedAtUtc : (DateTime?)null;
        if (to is { } end && from >= end)
            return;

        segments.Add(new DeviceClockSegment
        {
            FromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc),
            ToUtc = to is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null,
            OffsetMinutes = offset,
            ObservationCount = run.Count,
        });
    }

    private static int Median(IReadOnlyList<int> sorted)
    {
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static int SnapToGranularity(int offsetMinutes) =>
        (int)Math.Round(offsetMinutes / (double)OffsetGranularityMinutes, MidpointRounding.AwayFromZero)
        * OffsetGranularityMinutes;
}
