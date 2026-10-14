namespace Nocturne.Core.Models.V4;

/// <summary>
/// Insulin-pump properties for a <see cref="DeviceCatalogEntry"/>: the manufacturer's rated wear
/// times for the parts that get changed.
/// </summary>
/// <seealso cref="DeviceCatalogEntry"/>
public record PumpProperties
{
    /// <summary>
    /// True for a patch pump (e.g. Omnipod), where the pod holds the insulin and the cannula and is
    /// changed as one; there is then no separate reservoir to change.
    /// </summary>
    public required bool IsPatchPump { get; init; }

    /// <summary>
    /// Rated wear time of the pod, or of the infusion set for a tubed pump, in hours.
    /// </summary>
    public required int SiteDurationHours { get; init; }

    /// <summary>
    /// Rated in-use time of the reservoir or cartridge in hours; null for a patch pump, and for a
    /// pump whose reservoir is rated only as changed when empty.
    /// </summary>
    public int? ReservoirDurationHours { get; init; }
}
