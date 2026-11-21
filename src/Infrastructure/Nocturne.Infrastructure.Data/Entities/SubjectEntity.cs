using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Subjects (users/devices) - enhanced from legacy Nightscout
/// Represents both human users and automated devices that can authenticate
/// </summary>
[Table("subjects")]
public class SubjectEntity : IEntityTimestamped
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Display name of the subject
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Login identifier for non-discoverable WebAuthn flows
    /// </summary>
    [MaxLength(50)]
    [Column("username")]
    public string? Username { get; set; }

    /// <summary>
    /// Email address (from OIDC claims or manually set)
    /// </summary>
    [MaxLength(255)]
    [Column("email")]
    public string? Email { get; private set; }

    /// <summary>
    /// When <see cref="Email"/> was confirmed as an address its owner controls; null while it is
    /// unconfirmed. Written only by <see cref="SetEmail"/> and <see cref="ConfirmEmail"/>.
    /// </summary>
    [Column("email_confirmed_at")]
    public DateTime? EmailConfirmedAt { get; private set; }

    /// <summary>
    /// Stores <paramref name="email"/> as the address. A different address, or none, clears
    /// <see cref="EmailConfirmedAt"/>: the confirmation was for the old address and says nothing
    /// about the new one. The same address (<see cref="EmailAddresses.AreSame"/>) keeps it. Every write of the address
    /// goes through here. Returns whether the address changed, in which case the caller's save
    /// must write both columns as a pair.
    /// </summary>
    public bool SetEmail(string? email)
    {
        var changed = !EmailAddresses.AreSame(Email, email);
        if (changed)
            EmailConfirmedAt = null;
        Email = email;
        return changed;
    }

    /// <summary>
    /// Confirms the address at <paramref name="now"/> when <paramref name="email"/> is the one
    /// stored, and returns whether it is. An address already confirmed keeps its first time.
    /// </summary>
    public bool ConfirmEmail(string? email, DateTime now)
    {
        if (!EmailAddresses.AreSame(Email, email))
            return false;
        EmailConfirmedAt ??= now;
        return true;
    }

    /// <summary>
    /// Notes or description about this subject
    /// </summary>
    [Column("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Whether this subject is currently active and can authenticate
    /// </summary>
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this is a system-generated subject (cannot be deleted)
    /// </summary>
    [Column("is_system_subject")]
    public bool IsSystemSubject { get; set; }

    /// <summary>
    /// Whether this subject is a demo tenant's shared visitor account.
    /// </summary>
    /// <remarks>
    /// Anyone can obtain a session for it (see <c>DemoSessionController</c>), so it is
    /// authenticated but stands for no one. Endpoints that treat "authenticated" as
    /// "a person who signed up" — creating a tenant, accepting an invite, requesting
    /// membership — must refuse it; see <c>DenyDemoSubjectAttribute</c>.
    /// </remarks>
    [Column("is_demo_subject")]
    public bool IsDemoSubject { get; set; }

    /// <summary>
    /// System tracking: when record was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this subject last logged in
    /// </summary>
    [Column("last_login_at")]
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Original MongoDB ObjectId for migration tracking
    /// </summary>
    [MaxLength(24)]
    [Column("original_id")]
    public string? OriginalId { get; set; }

    /// <summary>
    /// User's preferred language code (e.g., "en", "fr", "de")
    /// </summary>
    [MaxLength(10)]
    [Column("preferred_language")]
    public string? PreferredLanguage { get; set; }

    /// <summary>
    /// Per-user display preferences (glucose units, time format, theme, chart style, etc.)
    /// serialized as a JSONB blob (<see cref="Core.Models.Configuration.UserDisplayPreferences"/>).
    /// Stored on the subject so preferences follow the user across devices and tenants.
    /// Null until the user first saves a preference.
    /// </summary>
    [Column("preferences")]
    public string? Preferences { get; set; }

    /// <summary>
    /// Approval status for access requests (e.g., "Approved", "Pending", "Denied")
    /// Defaults to "Approved" for existing subjects
    /// </summary>
    [Required]
    [MaxLength(20)]
    [Column("approval_status")]
    public string ApprovalStatus { get; set; } = "Approved";

    /// <summary>
    /// Optional message submitted with an access request
    /// </summary>
    [MaxLength(500)]
    [Column("access_request_message")]
    public string? AccessRequestMessage { get; set; }

    /// <summary>
    /// Whether this subject has platform-level admin access (not tenant-scoped).
    /// Platform admins can manage OIDC providers and other platform-wide configuration.
    /// </summary>
    [Column("is_platform_admin")]
    public bool IsPlatformAdmin { get; set; } = false;

    /// <summary>
    /// URL to the subject's avatar image, if set
    /// </summary>
    [MaxLength(2048)]
    [Column("avatar_url")]
    public string? AvatarUrl { get; set; }

    // Navigation properties

    /// <summary>
    /// Subject-role mappings for this subject
    /// </summary>
    public ICollection<SubjectRoleEntity> SubjectRoles { get; set; } = new List<SubjectRoleEntity>();

    /// <summary>
    /// Refresh tokens issued to this subject
    /// </summary>
    public ICollection<RefreshTokenEntity> RefreshTokens { get; set; } = new List<RefreshTokenEntity>();

    /// <summary>
    /// Passkey credentials registered by this subject
    /// </summary>
    public ICollection<PasskeyCredentialEntity> PasskeyCredentials { get; set; } = new List<PasskeyCredentialEntity>();

    /// <summary>
    /// TOTP credentials registered by this subject
    /// </summary>
    public ICollection<TotpCredentialEntity> TotpCredentials { get; set; } = new List<TotpCredentialEntity>();

    /// <summary>
    /// OIDC provider identities linked to this subject
    /// </summary>
    public ICollection<SubjectOidcIdentityEntity> OidcIdentities { get; set; } = new List<SubjectOidcIdentityEntity>();
}
