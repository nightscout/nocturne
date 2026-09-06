namespace Nocturne.API.Models.Responses;

/// <summary>
/// The current reservoir level resolved by <c>IReservoirEstimationService</c>.
/// <see cref="Found"/> is false when no snapshot in the estimation window carries a
/// reservoir value; the remaining fields are then unset.
/// </summary>
public class ReservoirEstimateResponse
{
    /// <summary>Whether an estimate could be resolved.</summary>
    public bool Found { get; set; }

    /// <summary>Remaining insulin in units. Null when <see cref="Found"/> is false.</summary>
    public decimal? Units { get; set; }

    /// <summary>True when the value only proves the reservoir holds at least this much
    /// (capped reading like "50+", or a bound overriding a lower estimate).</summary>
    public bool IsLowerBound { get; set; }

    /// <summary>True when the value was projected from an older reading rather than
    /// taken directly from the newest snapshot.</summary>
    public bool IsEstimated { get; set; }
}
