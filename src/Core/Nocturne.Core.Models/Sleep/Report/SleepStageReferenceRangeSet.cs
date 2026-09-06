using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.Sleep.Report;

/// <summary>
/// Normative reference ranges (percent of total sleep) for each sleep stage, resolved for a given
/// age and biological sex. Included on sleep reports so the frontend renders the "typical range"
/// band without hardcoding thresholds.
/// </summary>
/// <remarks>
/// Bands are curated from Ohayon et al. (2004), "Meta-Analysis of Quantitative Sleep Parameters
/// From Childhood to Old Age in Healthy Individuals" (SLEEP 27(7):1255-1273) — normative slow-wave
/// (deep), REM, and wake proportions across the lifespan (65 studies, 3,577 healthy subjects, ages
/// 5-102) — combined with AASM adult scoring norms. Two robust effects are captured: slow-wave and
/// REM sleep decline with age while wake increases, and women retain more slow-wave sleep than men.
/// Light is the N1+N2 remainder. When age or sex is unknown the set falls back to adult-female norms.
/// </remarks>
public class SleepStageReferenceRangeSet
{
    public double DeepMin { get; init; }
    public double DeepMax { get; init; }
    public double RemMin { get; init; }
    public double RemMax { get; init; }
    public double LightMin { get; init; }
    public double LightMax { get; init; }
    public double AwakeMin { get; init; }
    public double AwakeMax { get; init; }

    /// <summary>
    /// Human-readable description of the cohort these norms describe, e.g. "adults 18-39",
    /// "female older adults (65+)". Rendered by the frontend so the panel names whose range it shows.
    /// </summary>
    public string Label { get; init; } = "";

    /// <summary>Adult-female norms — the fallback used whenever age or sex is unknown.</summary>
    public static readonly SleepStageReferenceRangeSet Default = Resolve(null, null);

    private enum AgeBand { Child, Adolescent, Adult, MiddleAged, OlderAdult }

    /// <summary>
    /// Resolves the normative range set for a patient of the given age (in years) and biological
    /// sex. Unknown age resolves to the adult band; unknown sex resolves to female norms — so the
    /// combined default is "adult female".
    /// </summary>
    public static SleepStageReferenceRangeSet Resolve(int? ageYears, BiologicalSex? sex)
    {
        // Treat a missing or nonsensical (e.g. future-dated DOB) age as unknown, which resolves to
        // the adult band but with a label that makes no age claim.
        var age = ageYears is int a && a >= 0 ? a : (int?)null;
        var band = age is not int y ? AgeBand.Adult
            : y < 13 ? AgeBand.Child
            : y < 18 ? AgeBand.Adolescent
            : y < 40 ? AgeBand.Adult
            : y < 65 ? AgeBand.MiddleAged
            : AgeBand.OlderAdult;
        var ageKnown = age is int;

        // Unknown sex is treated as female (the default reference cohort).
        var isMale = sex == BiologicalSex.Male;

        // Deep (slow-wave/N3) and REM decline with age; women retain more of both. Light (N1+N2)
        // and wake are sex-neutral within a band. Values are percent of total sleep.
        var (deepMin, deepMax, remMin, remMax) = band switch
        {
            AgeBand.Child      => isMale ? (20, 30, 19, 24) : (20, 30, 20, 25),
            AgeBand.Adolescent => isMale ? (15, 24, 19, 24) : (16, 26, 20, 25),
            AgeBand.Adult      => isMale ? (11, 21, 19, 24) : (12, 23, 20, 25),
            AgeBand.MiddleAged => isMale ? (7, 16, 17, 22)  : (9, 18, 18, 23),
            _                  => isMale ? (3, 11, 15, 21)  : (5, 14, 16, 22),
        };

        var (lightMin, lightMax, awakeMin, awakeMax) = band switch
        {
            AgeBand.Child      => (40, 50, 0, 5),
            AgeBand.Adolescent => (43, 53, 0, 6),
            AgeBand.Adult      => (45, 55, 0, 8),
            AgeBand.MiddleAged => (48, 58, 3, 12),
            _                  => (50, 62, 5, 18),
        };

        // The "18-39" bracket only appears when age is actually known — the adult band is also the
        // unknown-age fallback, so an unknown age reads as plain "adults"/"women"/"men".
        var adultAge = ageKnown ? " 18-39" : "";
        var label = sex is null
            ? band switch
            {
                AgeBand.Child      => "children",
                AgeBand.Adolescent => "teenagers",
                AgeBand.Adult      => $"adults{adultAge}",
                AgeBand.MiddleAged => "adults 40-64",
                _                  => "older adults (65+)",
            }
            : band switch
            {
                AgeBand.Child      => isMale ? "boys" : "girls",
                AgeBand.Adolescent => isMale ? "teenage boys" : "teenage girls",
                AgeBand.Adult      => isMale ? $"men{adultAge}" : $"women{adultAge}",
                AgeBand.MiddleAged => isMale ? "men 40-64" : "women 40-64",
                _                  => isMale ? "men 65+" : "women 65+",
            };

        return new SleepStageReferenceRangeSet
        {
            DeepMin  = deepMin,  DeepMax  = deepMax,
            RemMin   = remMin,   RemMax   = remMax,
            LightMin = lightMin, LightMax = lightMax,
            AwakeMin = awakeMin, AwakeMax = awakeMax,
            Label    = label,
        };
    }
}
