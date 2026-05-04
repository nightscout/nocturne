using Nocturne.Core.Models.V4;

namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for creating a new insulin bolus record via the V4 API.
/// </summary>
/// <seealso cref="Validators.V4.CreateBolusRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Treatments.BolusController"/>
public class CreateBolusRequest
{
    /// <summary>
    /// When the bolus was delivered.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the device that delivered the bolus (e.g. pump serial number).
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier; required when <see cref="SyncIdentifier"/> is supplied.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Total insulin amount in units.
    /// </summary>
    public double Insulin { get; set; }

    /// <summary>
    /// Programmed insulin amount in units (may differ from delivered for interrupted boluses).
    /// </summary>
    public double? Programmed { get; set; }

    /// <summary>
    /// Actually delivered insulin amount in units.
    /// </summary>
    public double? Delivered { get; set; }

    /// <summary>
    /// Bolus delivery pattern (normal, square wave, dual wave, etc.).
    /// </summary>
    public BolusType? BolusType { get; set; }

    /// <summary>
    /// Whether this bolus was manually entered or originated from a pump/loop system.
    /// </summary>
    /// <value>Defaults to <see cref="BolusKind.Manual"/>.</value>
    public BolusKind Kind { get; set; } = BolusKind.Manual;

    /// <summary>
    /// Whether this bolus was delivered automatically by an APS/loop system.
    /// </summary>
    public bool Automatic { get; set; }

    /// <summary>
    /// Extended/square bolus duration in minutes.
    /// </summary>
    public double? Duration { get; set; }

    /// <summary>
    /// Upstream sync identifier for deduplication, paired with <see cref="DataSource"/>.
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Type or brand of insulin used (e.g. "Humalog", "NovoRapid").
    /// </summary>
    public string? InsulinType { get; set; }

    /// <summary>
    /// Optional reference to a <see cref="PatientInsulin"/>. When provided, the server
    /// resolves it to a <see cref="TreatmentInsulinContext"/> snapshot and overwrites
    /// <see cref="InsulinType"/> with the insulin's name.
    /// </summary>
    public Guid? PatientInsulinId { get; set; }

    /// <summary>
    /// Insulin on board (unabsorbed) at the time of the bolus, in units.
    /// </summary>
    public double? Unabsorbed { get; set; }

    /// <summary>
    /// Links this bolus to the bolus calculation that recommended it.
    /// </summary>
    public Guid? BolusCalculationId { get; set; }

    /// <summary>
    /// Links this bolus to the APS decision snapshot that triggered it.
    /// </summary>
    public Guid? ApsSnapshotId { get; set; }

    /// <summary>
    /// Correlation identifier for grouping related events (e.g. a meal bolus and carb intake).
    /// </summary>
    public Guid? CorrelationId { get; set; }
}
