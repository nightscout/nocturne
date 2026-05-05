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
public class BasalInjection : IV4Record
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public long Mills => new DateTimeOffset(Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
    public int? UtcOffset { get; set; }

    public string? Device { get; set; }
    public string? App { get; set; }
    public string? DataSource { get; set; }
    public string? SyncIdentifier { get; set; }

    public Guid? CorrelationId { get; set; }
    public string? LegacyId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }

    /// <summary>Insulin units injected.</summary>
    public double Units { get; set; }

    /// <summary>Optional user-supplied note.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Snapshot of the patient's insulin pharmacokinetic settings at injection time.
    /// Required: long-acting injections must reference a known PatientInsulin with role Basal or Both.
    /// </summary>
    public TreatmentInsulinContext InsulinContext { get; set; } = null!;

    /// <summary>
    /// Catch-all for fields not mapped to dedicated columns.
    /// </summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
