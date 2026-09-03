namespace Nocturne.Core.Models.Timezones;

/// <summary>
/// Turns raw connector records that carry two clocks — a fake-UTC clinical timestamp (device wall
/// clock stamped as UTC) and a real-UTC upload timestamp — into <see cref="DeviceClockObservation"/>s.
///
/// For each record, <c>clinical − upload = offset − uploadLag</c> with <c>uploadLag ≥ 0</c>, so the
/// difference is a hard lower bound on the device's effective UTC offset. Records sharing an upload
/// timestamp form a batch (a backlog flush); within a batch the newest record has the smallest lag,
/// so the batch's bound is the tightest available. When a batch is dense and evenly spaced (a prompt
/// CGM upload), the newest record was created essentially at upload time, and adding the sampling
/// interval turns the bound into a two-sided estimate.
/// </summary>
public static class DeviceClockEstimator
{
    /// <summary>Minimum records in a batch before its spacing is trusted for a two-sided estimate.</summary>
    public const int DenseMinSamples = 6;

    /// <summary>Maximum median spacing (minutes) for a batch to count as a dense prompt upload.</summary>
    public const int DenseMaxSpacingMinutes = 6;

    /// <summary>
    /// Sanity cap: no real device offset exceeds this. Batches whose bound falls outside are dropped
    /// as corrupt rather than recorded.
    /// </summary>
    public const int MaxPlausibleOffsetMinutes = 18 * 60;

    /// <summary>
    /// Groups records into upload batches and produces one observation per batch, ordered by
    /// observation time.
    /// </summary>
    /// <param name="connector">Connector id the observations are scoped to.</param>
    /// <param name="samples">
    /// (clinical fake-UTC wall clock, real-UTC upload time) pairs. Kind is ignored on both; only the
    /// clock readings matter.
    /// </param>
    public static IReadOnlyList<DeviceClockObservation> FromUploadBatches(
        string connector,
        IEnumerable<(DateTime ClinicalFakeUtc, DateTime UploadedAtUtc)> samples)
    {
        var observations = new List<DeviceClockObservation>();

        foreach (var batch in samples.GroupBy(s => s.UploadedAtUtc))
        {
            var clinical = batch.Select(s => s.ClinicalFakeUtc).OrderBy(t => t).ToList();
            var newest = clinical[^1];
            var boundMinutes = (int)Math.Round((newest - batch.Key).TotalMinutes);

            if (Math.Abs(boundMinutes) > MaxPlausibleOffsetMinutes)
                continue;

            var offsetMinutes = boundMinutes;
            var isEstimate = false;
            if (clinical.Count >= DenseMinSamples)
            {
                var spacing = MedianSpacingMinutes(clinical);
                if (spacing is > 0 and <= DenseMaxSpacingMinutes)
                {
                    offsetMinutes = boundMinutes + (int)Math.Round(spacing.Value);
                    isEstimate = true;
                }
            }

            observations.Add(new DeviceClockObservation
            {
                Connector = connector,
                Source = DeviceClockObservationSource.UploadBatch,
                ObservedAtUtc = DateTime.SpecifyKind(batch.Key, DateTimeKind.Utc),
                OffsetMinutes = offsetMinutes,
                IsEstimate = isEstimate,
                SampleCount = clinical.Count,
                CoversFromUtc = DateTime.SpecifyKind(clinical[0].AddMinutes(-offsetMinutes), DateTimeKind.Utc),
            });
        }

        return observations.OrderBy(o => o.ObservedAtUtc).ToList();
    }

    private static double? MedianSpacingMinutes(IReadOnlyList<DateTime> ordered)
    {
        if (ordered.Count < 2)
            return null;

        var gaps = new List<double>(ordered.Count - 1);
        for (var i = 1; i < ordered.Count; i++)
            gaps.Add((ordered[i] - ordered[i - 1]).TotalMinutes);

        gaps.Sort();
        var mid = gaps.Count / 2;
        return gaps.Count % 2 == 1 ? gaps[mid] : (gaps[mid - 1] + gaps[mid]) / 2;
    }
}
