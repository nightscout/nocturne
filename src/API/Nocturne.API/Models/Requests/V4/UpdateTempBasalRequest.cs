using Nocturne.Core.Models.V4;

namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for updating an existing temporary basal span via the V4 API.
/// The span is identified by the route-level ID parameter.
/// </summary>
/// <remarks>
/// The update sets an explicit <see cref="Rate"/>, so a span that was set by a percent of the schedule
/// becomes a fixed-rate span.
/// </remarks>
/// <seealso cref="Nocturne.API.Controllers.V4.Treatments.TempBasalController"/>
/// <seealso cref="TempBasal"/>
public class UpdateTempBasalRequest : IV4RecordHeader
{
    /// <summary>
    /// When the temp basal started.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the pump that ran the temp basal.
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
    /// Correlation identifier for grouping related events.
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Temporary basal rate in units per hour. Zero is valid (zero-temp).
    /// </summary>
    public double Rate { get; set; }

    /// <summary>
    /// Planned duration in minutes; the span end is computed as timestamp + duration.
    /// Omit for an open-ended span.
    /// </summary>
    public double? DurationMinutes { get; set; }

    /// <summary>
    /// Scheduled basal rate that this temp basal overrides, when known.
    /// </summary>
    public double? ScheduledRate { get; set; }

    /// <summary>
    /// Origin of this temp basal. When omitted the stored origin is kept.
    /// </summary>
    public TempBasalOrigin? Origin { get; set; }

    /// <summary>
    /// Pump-specific record identifier, when the pump reports one.
    /// </summary>
    public string? PumpRecordId { get; set; }

    /// <summary>
    /// Links this temp basal to the APS decision snapshot that enacted it.
    /// </summary>
    public Guid? ApsSnapshotId { get; set; }
}
