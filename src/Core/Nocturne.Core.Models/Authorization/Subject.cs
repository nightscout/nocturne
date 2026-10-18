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
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Subject type (user, device, service)
    /// </summary>
    public SubjectType Type { get; set; }

    /// <summary>
    /// Email address (for users)
    /// </summary>
    public string? Email { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this is a system-generated subject (cannot be deleted)
    /// </summary>
    public bool IsSystemSubject { get; set; }

    /// <summary>
    /// Whether this subject has platform-level admin access
    /// </summary>
    public bool IsPlatformAdmin { get; set; }

    public string? AvatarUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public List<Role> Roles { get; set; } = new();

    /// <summary>
    /// Aggregated permissions from all roles
    /// </summary>
    public List<string> Permissions { get; set; } = new();

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

    Admin
}
