using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Discrete long-acting basal insulin injection (MDI).
/// Conceptually parallel to <see cref="BasalSchedule"/> for pump users:
/// represents baseline coverage, not stacking IOB.
/// </summary>
/// <seealso cref="BasalSchedule"/>
/// <seealso cref="PatientInsulin"/>
/// <seealso cref="TreatmentInsulinContext"/>
/// <seealso cref="IV4Record"/>
[JsonSchemaFlatten]
public class BasalInjection : V4RecordBase, IDeviceAttributed
{
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="PatientDevice"/> (pen) this injection is attributed to.
    /// </summary>
    public Guid? PatientDeviceId { get; set; }

    /// <summary>Insulin units injected.</summary>
    public double Units { get; set; }

    /// <summary>Optional user-supplied note.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Snapshot of the patient's insulin pharmacokinetic settings at injection time.
    /// Optional: populated when the write referenced a known PatientInsulin with role Basal or
    /// Both, and left <c>null</c> when the writer (typically an uploader client) knows nothing
    /// about the patient's insulin catalog. Same shape as <see cref="Bolus.InsulinContext"/>.
    /// </summary>
    public TreatmentInsulinContext? InsulinContext { get; set; }
}
