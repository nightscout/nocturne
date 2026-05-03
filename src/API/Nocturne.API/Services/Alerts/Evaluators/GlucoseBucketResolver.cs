using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Computes the <see cref="GlucoseBucket"/> for a glucose reading given a single
/// <see cref="TargetRangeEntry"/> boundary set. Boundary semantics follow clinical
/// convention: <c>54</c> is <see cref="GlucoseBucket.Low"/> (not VeryLow), <c>70</c>/<c>140</c>
/// are <see cref="GlucoseBucket.TightRange"/>, <c>141</c>/<c>180</c> are
/// <see cref="GlucoseBucket.InRange"/>, <c>181</c>/<c>250</c> are <see cref="GlucoseBucket.High"/>,
/// <c>251</c>+ is <see cref="GlucoseBucket.VeryHigh"/>.
/// </summary>
public static class GlucoseBucketResolver
{
    /// <summary>Default VeryLow boundary (mg/dL) when the schedule entry has none.</summary>
    public const short DefaultVeryLow = 54;
    /// <summary>Default TightLow boundary (mg/dL) when the schedule entry has none — also doubles
    /// as the in-range "Low" boundary in legacy schedules without VeryLow/TightLow set.</summary>
    public const short DefaultTightLow = 70;
    /// <summary>Default TightHigh boundary (mg/dL) when the schedule entry has none.</summary>
    public const short DefaultTightHigh = 140;
    /// <summary>Default VeryHigh boundary (mg/dL) when the schedule entry has none.</summary>
    public const short DefaultVeryHigh = 250;

    /// <summary>
    /// Computes the bucket for <paramref name="glucoseMgdl"/> using the provided boundaries.
    /// Falls back to clinical defaults for any null bucket boundary.
    /// </summary>
    /// <param name="glucoseMgdl">Current glucose in mg/dL.</param>
    /// <param name="lowMgdl">Low (in-range lower bound) from the active TargetRangeEntry.</param>
    /// <param name="highMgdl">High (in-range upper bound) from the active TargetRangeEntry.</param>
    /// <param name="veryLow">Optional VeryLow override.</param>
    /// <param name="tightHigh">Optional TightHigh override (TightLow is implicit at <paramref name="lowMgdl"/>).</param>
    /// <param name="veryHigh">Optional VeryHigh override.</param>
    public static GlucoseBucket Compute(
        decimal glucoseMgdl,
        decimal lowMgdl,
        decimal highMgdl,
        short? veryLow,
        short? tightHigh,
        short? veryHigh)
    {
        var vLow = (decimal)(veryLow ?? DefaultVeryLow);
        var tHigh = (decimal)(tightHigh ?? DefaultTightHigh);
        var vHigh = (decimal)(veryHigh ?? DefaultVeryHigh);

        // Boundary semantics (closed lower bound for each bucket above VeryLow):
        //   < vLow              -> VeryLow         (54 mg/dL is therefore Low, not VeryLow)
        //   [vLow, lowMgdl)     -> Low             (e.g. [54, 70))
        //   [lowMgdl, tHigh]    -> TightRange      (70..140 inclusive)
        //   (tHigh, highMgdl]   -> InRange         (141..180 inclusive)
        //   (highMgdl, vHigh]   -> High            (181..250 inclusive)
        //   > vHigh             -> VeryHigh        (251+)
        if (glucoseMgdl < vLow) return GlucoseBucket.VeryLow;
        if (glucoseMgdl < lowMgdl) return GlucoseBucket.Low;
        if (glucoseMgdl <= tHigh) return GlucoseBucket.TightRange;
        if (glucoseMgdl <= highMgdl) return GlucoseBucket.InRange;
        if (glucoseMgdl <= vHigh) return GlucoseBucket.High;
        return GlucoseBucket.VeryHigh;
    }
}
