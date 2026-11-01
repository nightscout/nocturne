using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Audit log for security events
/// Tracks all authentication-related events for security monitoring
/// </summary>
/// <remarks>
/// Deliberately not <c>ITenantScoped</c>: failed logins and everything else that happens before a
/// tenant is resolved must still be recordable, and a tenant query filter plus RLS policy would
/// make those rows unwritable and unreadable exactly when they matter. <see cref="TenantId"/> is a
/// plain nullable column with no foreign key for the same reason — the trail outlives the tenant.
/// <see cref="TargetSubjectId"/> and <see cref="ActorSubjectId"/> carry no foreign key either: the trail
/// also outlives the subject, so the rows an insider incident needs survive the account's deletion
/// as a pseudonymous record once the subject's personal data is cleared from them.
/// </remarks>
[Table("auth_audit_log")]
public class AuthAuditLogEntity : IEntityCreated
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Event type: 'login', 'logout', 'token_issued', 'token_revoked', 'failed_auth',
    /// 'permission_denied', 'role_assigned', 'role_removed', 'subject_created', 'subject_deleted'
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// The subject the event happened to, if any. See <see cref="Nocturne.Core.Contracts.Auth.IAuthAuditService"/>.
    /// </summary>
    [Column("target_subject_id")]
    public Guid? TargetSubjectId { get; set; }

    /// <summary>
    /// The caller who performed the event. Mirrors <see cref="TargetSubjectId"/> when a subject
    /// acted for themselves, so "everything this subject did" is one indexed predicate.
    /// </summary>
    [Column("actor_subject_id")]
    public Guid? ActorSubjectId { get; set; }

    /// <summary>
    /// Identifies the credential that performed this event when the caller has no subject of its
    /// own, e.g. an instance-key provisioner. Never holds key material.
    /// </summary>
    /// <seealso cref="Nocturne.Core.Models.Authorization.AuthContext.CredentialFingerprint"/>
    [MaxLength(64)]
    [Column("actor_credential")]
    public string? ActorCredential { get; set; }

    /// <summary>
    /// The tenant this event targeted, if any. Differs from the actor's own tenant on
    /// administrative actions, and is null for events that precede tenant resolution.
    /// </summary>
    [Column("tenant_id")]
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Foreign key to the refresh token involved in this event (if applicable)
    /// </summary>
    [Column("refresh_token_id")]
    public Guid? RefreshTokenId { get; set; }

    /// <summary>
    /// Navigation property to the refresh token
    /// </summary>
    public RefreshTokenEntity? RefreshToken { get; set; }

    /// <summary>
    /// IP address of the client (supports IPv4 and IPv6)
    /// </summary>
    [MaxLength(45)]
    [Column("ip_address")]
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent string from the client
    /// </summary>
    [Column("user_agent")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// Additional event details (JSON)
    /// Example: {"permission": "api:entries:read", "resource": "/api/v1/entries"}
    /// </summary>
    [Column("details", TypeName = "jsonb")]
    public string? DetailsJson { get; set; }

    /// <summary>
    /// Whether this event was successful
    /// </summary>
    [Column("success")]
    public bool Success { get; set; } = true;

    /// <summary>
    /// Error message if the event was not successful
    /// </summary>
    [MaxLength(500)]
    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Request trace identifier for tracing related events
    /// </summary>
    [MaxLength(50)]
    [Column("correlation_id")]
    public string? TraceId { get; set; }

    /// <summary>
    /// When this event occurred
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
