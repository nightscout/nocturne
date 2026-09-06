using Nocturne.Core.Models.V4;

namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting a blood glucose (BG) check record via the V4 API.
/// </summary>
/// <seealso cref="Validators.V4.UpsertBGCheckRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Glucose.BGCheckController"/>
public class UpsertBGCheckRequest : IBulkUpsertRequest
{
    /// <summary>
    /// When the BG check was performed.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the device that performed the check.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Blood glucose reading value (validated 0-10,000).
    /// </summary>
    public double Glucose { get; set; }

    /// <summary>
    /// Unit of the glucose reading (mg/dL or mmol/L).
    /// </summary>
    public GlucoseUnit? Units { get; set; }

    /// <summary>
    /// Origin of the glucose value (finger stick, sensor, manual, etc.).
    /// </summary>
    public GlucoseType? GlucoseType { get; set; }

    /// <summary>
    /// Upstream sync identifier for deduplication.
    /// </summary>
    public string? SyncIdentifier { get; set; }
}
