namespace Nocturne.Core.Models.V4;

/// <summary>
/// How a <see cref="TempBasal"/>'s <see cref="TempBasal.Origin"/> was decided, so a default can be
/// told from a fact. Only <see cref="Uploader"/> is ever revisited.
/// </summary>
/// <seealso cref="TempBasalOriginProvenance"/>
public enum TempBasalOriginEvidence
{
    /// <summary>
    /// Stated by the upload (its <c>automatic</c> flag, a suspend <c>reason</c> or AAPS <c>type</c>),
    /// by a connector, or by an edit that names it.
    /// </summary>
    Reported,

    /// <summary>The default for who uploaded it, not yet checked against the APS snapshots.</summary>
    Uploader,

    /// <summary>
    /// Decided by the APS snapshots its device uploaded around its start: one proposed its rate
    /// (<see cref="TempBasal.ApsSnapshotId"/> names it), or none did and it was set by hand.
    /// </summary>
    Snapshot,

    /// <summary>The default for who uploaded it, which no snapshot of its device was near enough to judge.</summary>
    Uncorroborated,
}
