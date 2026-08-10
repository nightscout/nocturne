using Nocturne.Core.Models.V4;

namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for creating a new basal insulin injection record via the V4 API.
/// </summary>
/// <seealso cref="Validators.V4.CreateBasalInjectionRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Treatments.BasalInjectionController"/>
public class CreateBasalInjectionRequest
{
    /// <summary>
    /// When the basal insulin was injected. Cannot be more than 5 minutes in the future.
    /// </summary>
    public required DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the device used to record the injection (e.g. "iPhone-app").
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
    /// Upstream sync identifier for deduplication, paired with <see cref="DataSource"/>.
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Optional reference to the <see cref="PatientInsulin"/> used for this injection. When
    /// supplied, the referenced insulin must exist, carry role <c>Basal</c> or <c>Both</c>, and be
    /// active at <see cref="Timestamp"/>; the server resolves it to a
    /// <see cref="TreatmentInsulinContext"/> snapshot at write time and rejects the request with
    /// <c>400 Bad Request</c> otherwise. When omitted, no insulin is resolved and the stored
    /// record's <see cref="BasalInjection.InsulinContext"/> stays <c>null</c> — the shape uploader
    /// clients produce when they know nothing about the patient's insulin catalog.
    /// </summary>
    public Guid? PatientInsulinId { get; set; }

    /// <summary>
    /// Insulin units injected. Must be greater than zero.
    /// </summary>
    public required double Units { get; set; }

    /// <summary>
    /// Optional free-text user note.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Correlation identifier for grouping related events.
    /// </summary>
    public Guid? CorrelationId { get; set; }
}
