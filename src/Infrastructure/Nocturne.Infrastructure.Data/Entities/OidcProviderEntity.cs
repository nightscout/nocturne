using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// OIDC Provider Configuration for multi-provider SSO support
/// Stores configuration for external identity providers
/// </summary>
[Table("oidc_providers")]
public class OidcProviderEntity : IHasCreatedAt, IHasUpdatedAt
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Display name for this provider (e.g., "Keycloak", "Azure AD")
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// OIDC issuer URL (e.g., "https://auth.example.com/realms/nocturne")
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Column("issuer_url")]
    public string IssuerUrl { get; set; } = string.Empty;

    /// <summary>
    /// OAuth2 client ID for this provider
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Column("client_id")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted OAuth2 client secret (encrypted at rest)
    /// </summary>
    [Column("client_secret_encrypted")]
    public byte[]? ClientSecretEncrypted { get; set; }

    /// <summary>
    /// Cached OIDC discovery document (JSON)
    /// </summary>
    [Column("discovery_document", TypeName = "jsonb")]
    public string? DiscoveryDocumentJson { get; set; }

    /// <summary>
    /// When the discovery document was last cached
    /// </summary>
    [Column("discovery_cached_at")]
    public DateTime? DiscoveryCachedAt { get; set; }

    /// <summary>
    /// OAuth2 scopes to request (stored as JSON array)
    /// </summary>
    [Column("scopes", TypeName = "jsonb")]
    public List<string> Scopes { get; set; } = new() { "openid", "profile", "email" };

    /// <summary>
    /// Claim mappings JSON (map OIDC claims to Nocturne claims)
    /// Example: {"email": "email", "name": "preferred_username"}
    /// </summary>
    [Column("claim_mappings", TypeName = "jsonb")]
    public string ClaimMappingsJson { get; set; } = "{}";

    /// <summary>
    /// Default roles to assign to users from this provider (stored as JSON array)
    /// </summary>
    [Column("default_roles", TypeName = "jsonb")]
    public List<string> DefaultRoles { get; set; } = new() { "readable" };

    /// <summary>
    /// Whether this provider is enabled and available for authentication
    /// </summary>
    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Display order for this provider in the login UI
    /// </summary>
    [Column("display_order")]
    public int DisplayOrder { get; set; } = 0;

    /// <summary>
    /// Optional icon URL or CSS class for the login button
    /// </summary>
    [MaxLength(500)]
    [Column("icon")]
    public string? Icon { get; set; }

    /// <summary>
    /// Optional button color for the login button (CSS color)
    /// </summary>
    [MaxLength(50)]
    [Column("button_color")]
    public string? ButtonColor { get; set; }

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
}
