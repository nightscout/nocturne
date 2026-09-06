using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.Analytics;

/// <summary>
/// Time-pairs two CGM devices' readings and measures how far apart they run.
/// </summary>
public static class CgmComparisonCalculator
{
    /// <summary>
    /// Pairs each device A reading with the nearest device B reading inside <paramref name="tolerance"/>
    /// and computes the agreement measures over the result. A device B reading may serve more than one
    /// device A reading when A samples faster; <see cref="CgmComparisonResult.UnpairedCountB"/> counts
    /// only the B readings no A reading matched. Two equidistant B readings tie to the earlier one.
    /// </summary>
    /// <remarks>
    /// Fills everything derived from the readings; the caller supplies the device identities and window.
    /// Non-positive values are dropped before pairing — they are not glucose, and they would divide into
    /// the relative measures.
    /// </remarks>
    public static CgmComparisonResult Compare(
        IEnumerable<SensorGlucose> readingsA,
        IEnumerable<SensorGlucose> readingsB,
        TimeSpan tolerance)
    {
        var a = readingsA.Where(r => r.Mgdl > 0).OrderBy(r => r.Timestamp).ToList();
        var b = readingsB.Where(r => r.Mgdl > 0).OrderBy(r => r.Timestamp).ToList();

        var pairs = new List<CgmPairedReading>(a.Count);
        var matchedB = new bool[b.Count];
        var unpairedA = 0;
        var floor = 0;

        foreach (var readingA in a)
        {
            // b[floor] is the last B reading at or before this A reading (or b[0] when every B is
            // later), so the nearest B is either it or its successor. A never moves backwards, so
            // the scan over B is a single pass across the whole of A.
            while (floor + 1 < b.Count && b[floor + 1].Timestamp <= readingA.Timestamp)
                floor++;

            var best = -1;
            var bestDelta = TimeSpan.MaxValue;
            for (var k = floor; k < b.Count && k <= floor + 1; k++)
            {
                var delta = (b[k].Timestamp - readingA.Timestamp).Duration();
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = k;
                }
            }

            if (best < 0 || bestDelta > tolerance)
            {
                unpairedA++;
                continue;
            }

            matchedB[best] = true;
            pairs.Add(new CgmPairedReading
            {
                TimestampA = readingA.Timestamp,
                TimestampB = b[best].Timestamp,
                MgdlA = readingA.Mgdl,
                MgdlB = b[best].Mgdl,
            });
        }

        return new CgmComparisonResult
        {
            ToleranceMinutes = tolerance.TotalMinutes,
            ReadingCountA = a.Count,
            ReadingCountB = b.Count,
            UnpairedCountA = unpairedA,
            UnpairedCountB = matchedB.Count(m => !m),
            Pairs = pairs,
            Metrics = Measure(pairs),
        };
    }

    /// <summary>
    /// Computes the agreement measures over an already-paired series, or null when nothing paired.
    /// Device B is the reference for every relative measure; see <see cref="CgmAgreementMetrics"/>.
    /// </summary>
    public static CgmAgreementMetrics? Measure(IReadOnlyList<CgmPairedReading> pairs)
    {
        if (pairs.Count == 0)
            return null;

        var absoluteSum = 0.0;
        var relativeSum = 0.0;
        var signedSum = 0.0;
        var within15 = 0;

        foreach (var pair in pairs)
        {
            var difference = pair.MgdlA - pair.MgdlB;
            var absolute = Math.Abs(difference);

            absoluteSum += absolute;
            relativeSum += absolute / pair.MgdlB;
            signedSum += difference;

            if (pair.MgdlB < 100 ? absolute <= 15 : absolute / pair.MgdlB <= 0.15)
                within15++;
        }

        return new CgmAgreementMetrics
        {
            PairCount = pairs.Count,
            MeanAbsoluteDifferenceMgdl = absoluteSum / pairs.Count,
            MardPercent = relativeSum / pairs.Count * 100,
            BiasMgdl = signedSum / pairs.Count,
            Within15Percent = (double)within15 / pairs.Count * 100,
        };
    }
}
