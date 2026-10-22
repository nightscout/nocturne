namespace Nocturne.Core.Models.V4;

/// <summary>
/// Insulin-pump-specific properties for a <see cref="DeviceCatalogEntry"/>, describing what wears out
/// and how often.
/// </summary>
/// <seealso cref="DeviceCatalogEntry"/>
/// <seealso cref="DeviceCatalog"/>
public record PumpProperties
{
    /// <summary>
    /// Whether the pump is a patch (pod) that carries its own cannula and reservoir and is replaced
    /// whole, rather than a tubed pump with a separate infusion set and refillable reservoir.
    /// </summary>
    public required bool IsPatch { get; init; }

    /// <summary>
    /// Approved wear of one pod, or of one infusion set on a tubed pump, in hours.
    /// </summary>
    public required int SiteDurationHours { get; init; }

    /// <summary>
    /// How long one reservoir is expected to last on a tubed pump, in hours. Null for patch pumps,
    /// whose reservoir goes with the pod.
    /// </summary>
    public int? ReservoirDurationHours { get; init; }
}
