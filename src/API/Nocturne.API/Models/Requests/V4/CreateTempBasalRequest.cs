using Nocturne.Core.Models.V4;

namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for writing a temporary basal rate span via the V4 API.
/// </summary>
/// <remarks>
/// Records carrying both <see cref="DataSource"/> and <see cref="SyncIdentifier"/> are upserted:
/// a row already matched by that pair is updated in place, so uploader retries stay idempotent.
///
/// A cancel (<see cref="IsCancel"/> = <see langword="true"/>) does not create a record: it
/// truncates the temp basal active at <see cref="Timestamp"/> by setting its end to that instant.
/// <see cref="Rate"/> and <see cref="DurationMinutes"/> are ignored for cancels.
/// </remarks>
/// <seealso cref="Nocturne.API.Controllers.V4.Treatments.TempBasalController"/>
/// <seealso cref="TempBasal"/>
public class CreateTempBasalRequest
{
    /// <summary>
    /// When the temp basal started (or, for cancels, when the active temp basal ends).
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
    /// Upstream data source identifier; required when SyncIdentifier is supplied.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Stable per-source identifier. When paired with DataSource, re-uploading the same
    /// temp basal updates the existing record in place rather than creating a duplicate.
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Correlation identifier for grouping related events (e.g. the APS snapshot that enacted it).
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Temporary basal rate in units per hour. Zero is valid (zero-temp).
    /// </summary>
    public double Rate { get; set; }

    /// <summary>
    /// Planned duration in minutes; the span end is computed as timestamp + duration.
    /// Omit for an open-ended span (ended later by a cancel or a superseding temp basal).
    /// </summary>
    public double? DurationMinutes { get; set; }

    /// <summary>
    /// Scheduled basal rate that this temp basal overrides, when known.
    /// </summary>
    public double? ScheduledRate { get; set; }

    /// <summary>
    /// Origin of this temp basal. Defaults to <see cref="TempBasalOrigin.Manual"/> when omitted;
    /// AID uploaders should send <see cref="TempBasalOrigin.Algorithm"/>.
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

    /// <summary>
    /// When true, truncates the temp basal active at <see cref="Timestamp"/> instead of
    /// creating a record. A cancel with no active temp basal is a no-op.
    /// </summary>
    public bool IsCancel { get; set; }
}
