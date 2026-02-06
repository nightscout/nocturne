using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Audit log for security events
/// Tracks all authentication-related events for security monitoring
/// </summary>
[Table("auth_audit_log")]
public class AuthAuditLogEntity : IHasCreatedAt
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
    /// Foreign key to the subject involved in this event (if applicable)
    /// </summary>
    [Column("subject_id")]
    public Guid? SubjectId { get; set; }

    /// <summary>
    /// Navigation property to the subject
    /// </summary>
    public SubjectEntity? Subject { get; set; }

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
    /// Correlation ID for tracing related events
    /// </summary>
    [MaxLength(50)]
    [Column("correlation_id")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// When this event occurred
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Constants for auth audit event types
/// </summary>
public static class AuthAuditEventType
{
    /// <summary>
    /// Event type for a successful login.
    /// </summary>
    public const string Login = "login";

    /// <summary>
    /// Event type for a logout.
    /// </summary>
    public const string Logout = "logout";

    /// <summary>
    /// Event type when an access token is issued.
    /// </summary>
    public const string TokenIssued = "token_issued";

    /// <summary>
    /// Event type when an access token is revoked.
    /// </summary>
    public const string TokenRevoked = "token_revoked";

    /// <summary>
    /// Event type when an access token is refreshed.
    /// </summary>
    public const string TokenRefreshed = "token_refreshed";

    /// <summary>
    /// Event type for a failed authentication attempt.
    /// </summary>
    public const string FailedAuth = "failed_auth";

    /// <summary>
    /// Event type when a permission check is denied.
    /// </summary>
    public const string PermissionDenied = "permission_denied";

    /// <summary>
    /// Event type when a role is assigned to a subject.
    /// </summary>
    public const string RoleAssigned = "role_assigned";

    /// <summary>
    /// Event type when a role is removed from a subject.
    /// </summary>
    public const string RoleRemoved = "role_removed";

    /// <summary>
    /// Event type when a subject (user) is created.
    /// </summary>
    public const string SubjectCreated = "subject_created";

    /// <summary>
    /// Event type when a subject is updated.
    /// </summary>
    public const string SubjectUpdated = "subject_updated";

    /// <summary>
    /// Event type when a subject is deleted.
    /// </summary>
    public const string SubjectDeleted = "subject_deleted";

    /// <summary>
    /// Event type when an API secret is used.
    /// </summary>
    public const string ApiSecretUsed = "api_secret_used";

    /// <summary>
    /// Event type when a session has expired.
    /// </summary>
    public const string SessionExpired = "session_expired";
}
