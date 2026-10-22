using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for <see cref="Core.Models.V4.ProfileActivation"/>. The inherited
/// <see cref="V4TimeSeriesEntityBase.Timestamp"/> is the start, mapped to <c>start_timestamp</c>.
/// </summary>
[Table("profile_activations")]
public class ProfileActivationEntity
    : V4TimeSeriesEntityBase, ISyncDedupable, IDeviceAttributedEntity, IUpstreamFingerprinted
{
    [Column("end_timestamp")]
    public DateTime? EndTimestamp { get; set; }

    [Column("phase")]
    [MaxLength(16)]
    public string Phase { get; set; } = null!;

    [Column("origin")]
    [MaxLength(16)]
    public string Origin { get; set; } = null!;

    [Column("base_profile_name")]
    [MaxLength(256)]
    public string BaseProfileName { get; set; } = null!;

    [Column("percentage")]
    public int Percentage { get; set; } = 100;

    [Column("timeshift_ms")]
    public long TimeshiftMs { get; set; }

    [Column("requested_duration_ms")]
    public long? RequestedDurationMs { get; set; }

    [Column("requested_activation_id")]
    public Guid? RequestedActivationId { get; set; }

    [Column("snapshot", TypeName = "jsonb")]
    public string? SnapshotJson { get; set; }

    [Column("snapshot_is_customized")]
    public bool SnapshotIsCustomized { get; set; }

    [Column("entered_by")]
    [MaxLength(256)]
    [FitToColumn]
    public string? EnteredBy { get; set; }

    [Column("ended_at")]
    public DateTime? EndedAt { get; set; }

    [Column("ended_by")]
    [MaxLength(256)]
    public string? EndedBy { get; set; }

    [Column("sync_identifier")]
    [MaxLength(256)]
    public string? SyncIdentifier { get; set; }

    [Column("device_id")]
    public Guid? DeviceId { get; set; }

    [Column("patient_device_id")]
    public Guid? PatientDeviceId { get; set; }

    /// <inheritdoc />
    [AuditIgnored]
    [Column("upstream_fingerprint")]
    public string? UpstreamFingerprint { get; set; }
}
