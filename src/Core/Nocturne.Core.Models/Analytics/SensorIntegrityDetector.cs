namespace Nocturne.Core.Models.Analytics;

/// <summary>
/// Detects time windows where CGM readings show directionally-incoherent ("yo-yo") motion that is
/// unlikely to be physiologic and could mislead automated insulin delivery (AID) systems.
/// </summary>
/// <remarks>
/// <para>
/// This is a faithful C# port of the published reference algorithm (cgm_cluster_detector_v5,
/// release v6.0 — © Dan Heller, MIT). It is a pure function over (timestamp, glucose) pairs: no
/// I/O, no persistence, no tenant context. Parity with the reference is enforced by golden-vector
/// tests in <c>Nocturne.Core.Models.Tests</c>.
/// </para>
/// <para>
/// It is a <b>post-hoc</b> safety analytic, not a real-time signal: cluster start boundaries are
/// back-dated once subsequent readings make the oscillation evident.
/// </para>
/// <para>
/// Several pandas semantics are reproduced deliberately: the first reading always yields a phantom
/// reversal (NaN ≠ NaN), a zero delta breaks a reversal chain, total-path sums only become valid one
/// point later than amplitude/reversal sums (the first diff is NaN), and any window spanning a
/// timestamp gap is suppressed. Internally <see cref="double.NaN"/> marks "not enough data", matching
/// pandas rolling windows with <c>min_periods</c>.
/// </para>
/// </remarks>
public static class SensorIntegrityDetector
{
    /// <summary>
    /// Detect clusters of directionally-incoherent CGM data.
    /// </summary>
    /// <param name="timestamps">UTC reading timestamps. Need not be sorted; rows are paired with
    /// <paramref name="glucose"/> by position, NaN glucose dropped, then sorted by time.</param>
    /// <param name="glucose">Glucose values in mg/dL, same length as <paramref name="timestamps"/>.</param>
    /// <param name="config">Detector configuration. Defaults to <see cref="DetectorConfig"/> defaults.</param>
    public static IReadOnlyList<GlucoseCluster> DetectClusters(
        IReadOnlyList<DateTime> timestamps,
        IReadOnlyList<double> glucose,
        DetectorConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        ArgumentNullException.ThrowIfNull(glucose);
        if (timestamps.Count != glucose.Count)
        {
            throw new ArgumentException("timestamps and glucose must have equal length.");
        }

        var cfg = config ?? new DetectorConfig();
        var (t, g) = CleanAndSort(timestamps, glucose);

        var clusters = ScoreClusters(t, g, cfg);
        return ChainPromote(clusters, cfg);
    }

    /// <summary>
    /// Find clusters followed by hypoglycemia within a configurable window, optionally requiring
    /// insulin dosing during the cluster.
    /// </summary>
    /// <param name="timestamps">UTC reading timestamps.</param>
    /// <param name="glucose">Glucose values in mg/dL.</param>
    /// <param name="options">Hypo-event search options.</param>
    /// <param name="config">Detector configuration (forwarded to <see cref="DetectClusters"/>).</param>
    /// <param name="insulin">Optional insulin doses for correlation.</param>
    public static IReadOnlyList<HypoEvent> FindHypoEvents(
        IReadOnlyList<DateTime> timestamps,
        IReadOnlyList<double> glucose,
        HypoEventOptions? options = null,
        DetectorConfig? config = null,
        IReadOnlyList<InsulinDose>? insulin = null)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        ArgumentNullException.ThrowIfNull(glucose);

        var clusters = DetectClusters(timestamps, glucose, config);
        return FindHypoEvents(clusters, timestamps, glucose, options, insulin);
    }

    /// <summary>
    /// Find hypo events for clusters that have already been detected, avoiding a second detection
    /// pass when the caller already holds the cluster list (e.g. to render it alongside).
    /// </summary>
    /// <param name="clusters">Clusters previously returned by <see cref="DetectClusters"/> over the
    /// same series and configuration.</param>
    /// <param name="timestamps">UTC reading timestamps (same series the clusters were detected from).</param>
    /// <param name="glucose">Glucose values in mg/dL.</param>
    /// <param name="options">Hypo-event search options.</param>
    /// <param name="insulin">Optional insulin doses for correlation.</param>
    public static IReadOnlyList<HypoEvent> FindHypoEvents(
        IReadOnlyList<GlucoseCluster> clusters,
        IReadOnlyList<DateTime> timestamps,
        IReadOnlyList<double> glucose,
        HypoEventOptions? options = null,
        IReadOnlyList<InsulinDose>? insulin = null)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(timestamps);
        ArgumentNullException.ThrowIfNull(glucose);

        var opts = options ?? new HypoEventOptions();
        var (t, g) = CleanAndSort(timestamps, glucose);

        // Insulin sorted by time, NaN units dropped — mirrors the reference's dropna + sort.
        var doses = (insulin ?? [])
            .Where(d => !double.IsNaN(d.Units))
            .OrderBy(d => d.Time)
            .ToList();

        var window = TimeSpan.FromHours(opts.WindowHours);
        var events = new List<HypoEvent>();

        foreach (var c in clusters)
        {
            if (c.Confidence < opts.MinConfidence)
            {
                continue;
            }

            var windowEnd = c.End + window;

            // Post-cluster readings: strictly after end, up to and including window end.
            double nadir = double.PositiveInfinity;
            DateTime nadirTime = default;
            var belowThreshold = 0;
            var hasPost = false;

            for (var i = 0; i < t.Length; i++)
            {
                if (t[i] <= c.End || t[i] > windowEnd)
                {
                    continue;
                }

                hasPost = true;
                if (g[i] < opts.HypoThresholdMgdl)
                {
                    belowThreshold++;
                }

                // First occurrence of the minimum wins (pandas idxmin semantics).
                if (g[i] < nadir)
                {
                    nadir = g[i];
                    nadirTime = t[i];
                }
            }

            if (!hasPost || belowThreshold == 0)
            {
                continue;
            }

            var insulinDuring = doses
                .Where(d => d.Time >= c.Start && d.Time <= c.End)
                .ToList();

            if (opts.RequireInsulin && insulinDuring.Count == 0)
            {
                continue;
            }

            events.Add(new HypoEvent
            {
                Cluster = c,
                NadirMgdl = nadir,
                NadirTime = nadirTime,
                TimeToNadirHours = (nadirTime - c.End).TotalHours,
                ReadingsBelowThreshold = belowThreshold,
                InsulinDuringCluster = insulinDuring,
            });
        }

        return events;
    }

    private static (DateTime[] T, double[] G) CleanAndSort(
        IReadOnlyList<DateTime> timestamps,
        IReadOnlyList<double> glucose)
    {
        var rows = new List<(DateTime T, double G)>(timestamps.Count);
        for (var i = 0; i < timestamps.Count; i++)
        {
            if (!double.IsNaN(glucose[i]))
            {
                rows.Add((timestamps[i], glucose[i]));
            }
        }

        // Stable sort by time. The reference uses pandas sort_values (non-stable); for unique
        // timestamps the result is identical, and a stable order is the deterministic choice.
        rows.Sort((a, b) => a.T.CompareTo(b.T));

        var t = new DateTime[rows.Count];
        var g = new double[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            (t[i], g[i]) = rows[i];
        }

        return (t, g);
    }

    private static List<GlucoseCluster> ScoreClusters(DateTime[] t, double[] g, DetectorConfig cfg)
    {
        var n = g.Length;
        var result = new List<GlucoseCluster>();
        if (n == 0)
        {
            return result;
        }

        var dtMin = EstimateDtMinutes(t);

        // Rolling window size in points. Floored in time (not raw points) so coarse sampling does
        // not inflate the window; hard minimum of 3 points for any rolling computation.
        var winPts = Math.Max(
            Math.Max(
                (int)Math.Round(cfg.WindowMinutes / dtMin, MidpointRounding.ToEven),
                (int)Math.Round(cfg.MinWindowMinutes / dtMin, MidpointRounding.ToEven)),
            3);

        // Consecutive deltas and their reversal flags. d[0] is NaN; sign(NaN) is NaN, and NaN-aware
        // inequality makes index 0 a phantom reversal exactly as in the reference.
        var d = new double[n];
        var sign = new double[n];
        d[0] = double.NaN;
        sign[0] = double.NaN;
        for (var i = 1; i < n; i++)
        {
            d[i] = g[i] - g[i - 1];
            sign[i] = Math.Sign(d[i]);
        }

        var rev = new int[n];
        for (var i = 0; i < n; i++)
        {
            var prev = i == 0 ? double.NaN : sign[i - 1];
            rev[i] = NotEqual(sign[i], prev) && NotEqualZero(sign[i]) && NotEqualZero(prev) ? 1 : 0;
        }

        // Gap masking: any window covering a reading gap larger than the effective threshold is
        // suppressed. The threshold scales with sampling rate.
        var gapThresh = Math.Max(cfg.GapThresholdMinutes, 2.5 * dtMin);
        var gapBreak = new bool[n];
        gapBreak[0] = false; // dt_gaps[0] is fillna(0) in the reference
        for (var i = 1; i < n; i++)
        {
            gapBreak[i] = (t[i] - t[i - 1]).TotalMinutes > gapThresh;
        }

        var gapInWindow = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var lo = Math.Max(0, i - winPts + 1);
            var masked = false;
            for (var j = lo; j <= i; j++)
            {
                if (gapBreak[j])
                {
                    masked = true;
                    break;
                }
            }

            gapInWindow[i] = masked;
        }

        // Rolling features. NaN marks "window not yet full" (min_periods) or "gap-masked".
        var revCnt = new double[n];
        var amp = new double[n];
        var incohRatio = new double[n];

        for (var i = 0; i < n; i++)
        {
            revCnt[i] = double.NaN;
            amp[i] = double.NaN;
            incohRatio[i] = 0.0; // fillna(0) default before the window is valid

            if (gapInWindow[i])
            {
                continue;
            }

            // amp and rev_cnt require a full window of win_pts points (valid from i == win_pts - 1).
            if (i >= winPts - 1)
            {
                var lo = i - winPts + 1;
                var sumRev = 0;
                var min = double.PositiveInfinity;
                var max = double.NegativeInfinity;
                for (var j = lo; j <= i; j++)
                {
                    sumRev += rev[j];
                    if (g[j] < min)
                    {
                        min = g[j];
                    }

                    if (g[j] > max)
                    {
                        max = g[j];
                    }
                }

                revCnt[i] = sumRev;
                amp[i] = max - min;
            }

            // sum_abs of deltas needs a full window of valid diffs. d[0] is NaN, so the earliest
            // fully-valid window ends at i == win_pts (one later than amp/rev_cnt).
            if (i >= winPts)
            {
                var lo = i - winPts + 1;
                var sumAbs = 0.0;
                for (var j = lo; j <= i; j++)
                {
                    sumAbs += Math.Abs(d[j]);
                }

                var net = Math.Abs(g[i] - g[i - (winPts - 1)]);
                incohRatio[i] = sumAbs == 0.0 ? 0.0 : (sumAbs - net) / sumAbs;
            }
        }

        // Threshold mask. NaN comparisons are false, so masked/short windows never qualify.
        var mask = new bool[n];
        for (var i = 0; i < n; i++)
        {
            mask[i] = revCnt[i] >= cfg.MinReversals
                      && amp[i] >= cfg.MinAmp
                      && incohRatio[i] >= cfg.IncoherenceRatioThreshold;
        }

        // Contiguous True runs → segments; back-date each start by win_pts - 1; merge overlaps only.
        var merged = MergeSegments(mask, winPts);

        foreach (var (s, e) in merged)
        {
            var durationMin = (t[e] - t[s]).TotalMinutes;
            if (durationMin < cfg.MinClusterMinutes)
            {
                continue;
            }

            var peakRev = NanMax(revCnt, s, e);
            var peakIncoh = NanMax(incohRatio, s, e);

            var score = (0.6 * (peakRev / Math.Max(cfg.MinReversals, 1)))
                        + (0.4 * (peakIncoh / Math.Max(cfg.IncoherenceRatioThreshold, 1e-6)));

            var confidence = score >= cfg.ConfHigh ? ClusterConfidence.High
                : score >= cfg.ConfMedium ? ClusterConfidence.Medium
                : ClusterConfidence.Low;

            double wMin = double.PositiveInfinity, wMax = double.NegativeInfinity, stepMax = 0.0;
            for (var j = s; j <= e; j++)
            {
                if (g[j] < wMin)
                {
                    wMin = g[j];
                }

                if (g[j] > wMax)
                {
                    wMax = g[j];
                }

                if (j > s)
                {
                    stepMax = Math.Max(stepMax, Math.Abs(g[j] - g[j - 1]));
                }
            }

            var ampVal = wMax - wMin;

            var spikePromoted = false;
            if (cfg.SpikeBumpEnabled && stepMax >= cfg.SpikeBumpDeltaMgdl
                && !NearCalibration(t[s], t[e], cfg))
            {
                confidence = PromoteOnce(confidence);
                spikePromoted = true;
            }

            result.Add(new GlucoseCluster
            {
                Start = t[s],
                End = t[e],
                MinMgdl = wMin,
                MaxMgdl = wMax,
                DurationMinutes = durationMin,
                Confidence = confidence,
                Diagnostics = new ClusterDiagnostics
                {
                    SamplingIntervalMinutes = dtMin,
                    WindowPoints = winPts,
                    PeakReversals = peakRev,
                    PeakIncoherenceRatio = peakIncoh,
                    Amplitude = ampVal,
                    MaxStep = stepMax,
                    SpikePromoted = spikePromoted,
                    ChainSize = 1,          // finalized in ChainPromote
                    ChainPromoted = false,
                },
            });
        }

        return result;
    }

    private static List<(int Start, int End)> MergeSegments(bool[] mask, int winPts)
    {
        var segments = new List<(int, int)>();
        var i = 0;
        var n = mask.Length;
        while (i < n)
        {
            if (!mask[i])
            {
                i++;
                continue;
            }

            var start = i;
            while (i + 1 < n && mask[i + 1])
            {
                i++;
            }

            segments.Add((start, i));
            i++;
        }

        var merged = new List<(int Start, int End)>();
        foreach (var (s0, e0) in segments)
        {
            var s = Math.Max(0, s0 - (winPts - 1)); // back-date start by the window length
            var e = e0;
            if (merged.Count > 0 && s <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, e));
            }
            else
            {
                merged.Add((s, e));
            }
        }

        return merged;
    }

    private static List<GlucoseCluster> ChainPromote(List<GlucoseCluster> clusters, DetectorConfig cfg)
    {
        var n = clusters.Count;
        if (n == 0)
        {
            return clusters;
        }

        var parent = new int[n];
        for (var i = 0; i < n; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
            {
                parent[rb] = ra;
            }
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var ci = clusters[i];
                var cj = clusters[j];
                double gapMinutes;
                if (ci.Start <= cj.End && cj.Start <= ci.End)
                {
                    gapMinutes = 0.0;
                }
                else
                {
                    gapMinutes = Math.Min(
                        Math.Abs((cj.Start - ci.End).TotalSeconds),
                        Math.Abs((ci.Start - cj.End).TotalSeconds)) / 60.0;
                }

                if (gapMinutes <= cfg.ChainRadiusMinutes)
                {
                    Union(i, j);
                }
            }
        }

        var componentSize = new Dictionary<int, int>();
        for (var i = 0; i < n; i++)
        {
            var root = Find(i);
            componentSize[root] = componentSize.GetValueOrDefault(root) + 1;
        }

        var result = new List<GlucoseCluster>(n);
        for (var i = 0; i < n; i++)
        {
            var c = clusters[i];
            var chainSize = componentSize[Find(i)];
            var promoted = chainSize >= cfg.ChainMinSize;

            result.Add(c with
            {
                Confidence = promoted ? PromoteOnce(c.Confidence) : c.Confidence,
                Diagnostics = c.Diagnostics with
                {
                    ChainSize = chainSize,
                    ChainPromoted = promoted,
                },
            });
        }

        return result;
    }

    private static bool NearCalibration(DateTime start, DateTime end, DetectorConfig cfg)
    {
        if (cfg.CalibrationTimestamps.Count == 0)
        {
            return false;
        }

        var grace = TimeSpan.FromMinutes(cfg.CalibrationGraceMinutes);
        foreach (var ct in cfg.CalibrationTimestamps)
        {
            if (ct - grace <= end && ct + grace >= start)
            {
                return true;
            }
        }

        return false;
    }

    private static double EstimateDtMinutes(DateTime[] t)
    {
        if (t.Length < 2)
        {
            return 5.0;
        }

        var diffs = new double[t.Length - 1];
        for (var i = 1; i < t.Length; i++)
        {
            diffs[i - 1] = (t[i] - t[i - 1]).TotalMinutes;
        }

        Array.Sort(diffs);
        var m = diffs.Length;
        var median = m % 2 == 1
            ? diffs[m / 2]
            : (diffs[(m / 2) - 1] + diffs[m / 2]) / 2.0;

        return median > 0 ? median : 5.0;
    }

    private static double NanMax(double[] values, int start, int end)
    {
        var max = double.NaN;
        for (var i = start; i <= end; i++)
        {
            if (double.IsNaN(values[i]))
            {
                continue;
            }

            if (double.IsNaN(max) || values[i] > max)
            {
                max = values[i];
            }
        }

        return max;
    }

    // NaN-aware inequalities matching IEEE/pandas: any comparison involving NaN is "not equal".
    private static bool NotEqual(double a, double b)
        => double.IsNaN(a) || double.IsNaN(b) || a != b;

    private static bool NotEqualZero(double a)
        => double.IsNaN(a) || a != 0.0;

    private static ClusterConfidence PromoteOnce(ClusterConfidence c) => c switch
    {
        ClusterConfidence.Low => ClusterConfidence.Medium,
        ClusterConfidence.Medium => ClusterConfidence.High,
        _ => ClusterConfidence.High,
    };
}
