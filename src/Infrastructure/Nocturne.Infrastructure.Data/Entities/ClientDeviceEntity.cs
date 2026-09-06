using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A registered client app install (Prelude, the desktop Companion) that can be an actuation target
/// for the alert engine. Distinct from the telemetry <see cref="V4.DeviceEntity"/>: this records an
/// OAuth-paired app instance and the capabilities it advertises (notify, torch, vibrate, …).
/// </summary>
/// <remarks>
/// Keyed for addressing on <c>(TenantId, InstallId)</c>: <see cref="InstallId"/> is a stable UUID the
/// app generates at first launch and persists locally, so two installs of the same app (which share
/// one OAuth client/grant) remain individually addressable. The app re-upserts itself on every
/// startup so the advertised <see cref="Capabilities"/> and <see cref="LastSeenAt"/> stay current.
/// </remarks>
[Table("client_devices")]
public class ClientDeviceEntity : ITenantScoped, IAuditable
{
    /// <summary>Unique identifier for the device registration.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Tenant this device belongs to.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// The subject (user) who paired this device. Used for the device-ownership check and to list a
    /// user's own devices. Note: actuation fan-out is tenant-broadcast (every device of the target
    /// kind in the tenant), not subject-scoped — the alert domain has no subject ownership.
    /// </summary>
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// The OAuth grant this device was paired under, for revoke-cascade (removing the app in
    /// "connected apps" should remove its devices). Reserved: not populated until the device
    /// management flow wires grant resolution.
    /// </summary>
    [Column("grant_id")]
    public Guid? GrantId { get; set; }

    /// <summary>Client-generated stable install identifier (UUID), unique per install within a tenant.</summary>
    [Column("install_id")]
    [MaxLength(64)]
    public string InstallId { get; set; } = string.Empty;

    /// <summary>Device kind discriminator (e.g. <c>prelude</c>, <c>companion</c>).</summary>
    [Column("kind")]
    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>User-facing label (defaults from the grant label, e.g. "Rhys's Pixel").</summary>
    [Column("label")]
    [MaxLength(255)]
    public string? Label { get; set; }

    /// <summary>
    /// Accepted actuation capabilities (a filtered subset of <c>DeviceCapabilities.Registry</c>):
    /// only those known, allowed for the kind, and covered by the grant's scopes. Stored as a
    /// PostgreSQL <c>text[]</c>.
    /// </summary>
    [Column("capabilities")]
    public string[] Capabilities { get; set; } = [];

    /// <summary>Last time the device registered or was seen online.</summary>
    [Column("last_seen_at")]
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the device was first registered.</summary>
    [AuditIgnored]
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the device registration was last updated.</summary>
    [AuditIgnored]
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
