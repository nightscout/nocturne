using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Short-lived state token issued by /connect and by the Discord OAuth2 finalize hop.
/// Holds the Discord user identity and intended tenant slug until the user completes
/// the link flow. Lives in the public schema (cross-tenant).
/// </summary>
[Table("chat_identity_pending_links")]
public class ChatIdentityPendingLinkEntity
{
    /// <summary>Cryptographically random token that acts as primary key and lookup handle.</summary>
    [Key, Column("token"), MaxLength(64)]
    public string Token { get; set; } = string.Empty;

    /// <summary>Chat platform identifier (e.g. "discord", "telegram").</summary>
    [Column("platform"), MaxLength(16)]
    public string Platform { get; set; } = string.Empty;

    /// <summary>User's unique ID on the chat platform.</summary>
    [Column("platform_user_id"), MaxLength(256)]
    public string PlatformUserId { get; set; } = string.Empty;

    /// <summary>The chat account's username as the platform reported it, shown when confirming the link.</summary>
    [Column("platform_user_name"), MaxLength(256)]
    public string? PlatformUserName { get; set; }

    /// <summary>Optional tenant slug if the user provided one in /connect; null means "any tenant".</summary>
    [Column("tenant_slug"), MaxLength(64)]
    public string? TenantSlug { get; set; }

    /// <summary>Where the token came from: "connect-slash" or "oauth2-finalize".</summary>
    [Column("source"), MaxLength(32)]
    public string Source { get; set; } = string.Empty;

    /// <summary>When this pending link token was issued.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Absolute expiry; tokens past this time are invalid and garbage-collected.</summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }
}
