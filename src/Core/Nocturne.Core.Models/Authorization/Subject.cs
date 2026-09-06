namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Domain model representing an authentication subject (user, device, or service).
/// </summary>
/// <seealso cref="SubjectType"/>
/// <seealso cref="Role"/>
/// <seealso cref="SubjectOidcIdentity"/>
/// <seealso cref="ApprovalStatus"/>
public class Subject
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Display name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Subject type (user, device, service)
    /// </summary>
    public SubjectType Type { get; set; }

    /// <summary>
    /// Email address (for users)
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Whether the subject is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this is a system-generated subject (cannot be deleted)
    /// </summary>
    public bool IsSystemSubject { get; set; }

    /// <summary>
    /// Whether this subject has platform-level admin access
    /// </summary>
    public bool IsPlatformAdmin { get; set; }

    /// <summary>
    /// Avatar URL
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// When the subject was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the subject last logged in
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Assigned roles
    /// </summary>
    public List<Role> Roles { get; set; } = new();

    /// <summary>
    /// Aggregated permissions from all roles
    /// </summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// Notes or description
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// User's preferred language code (e.g., "en", "fr", "de")
    /// </summary>
    public string? PreferredLanguage { get; set; }

    /// <summary>
    /// Per-user display preferences serialized as a JSON blob (see
    /// <see cref="Configuration.UserDisplayPreferences"/>). Null until first saved.
    /// </summary>
    public string? Preferences { get; set; }
}

/// <summary>
/// Type of subject
/// </summary>
public enum SubjectType
{
    /// <summary>
    /// Human user (authenticated via OIDC)
    /// </summary>
    User,

    /// <summary>
    /// Device (e.g., insulin pump, CGM)
    /// </summary>
    Device,

    /// <summary>
    /// External service with API access
    /// </summary>
    Service,

    /// <summary>
    /// Admin user
    /// </summary>
    Admin
}
