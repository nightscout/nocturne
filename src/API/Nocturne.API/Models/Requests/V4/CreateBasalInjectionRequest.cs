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
    /// Reference to the <see cref="PatientInsulin"/> used for this injection. The referenced
    /// insulin's role must be <c>Basal</c> or <c>Both</c>. The server resolves this to a
    /// <see cref="TreatmentInsulinContext"/> snapshot at write time.
    /// </summary>
    public required Guid PatientInsulinId { get; set; }

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
