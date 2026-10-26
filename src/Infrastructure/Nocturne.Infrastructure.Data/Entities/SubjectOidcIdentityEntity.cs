using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Join table linking a subject to one or more OIDC provider identities.
/// Replaces the scalar OidcSubjectId/OidcIssuer columns on SubjectEntity.
/// </summary>
[Table("subject_oidc_identities")]
public class SubjectOidcIdentityEntity
{
    /// <summary>Primary key (UUID v7).</summary>
    [Key, Column("id")]
    public Guid Id { get; set; }

    /// <summary>FK to the Nocturne subject this identity belongs to.</summary>
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>FK to the configured OIDC provider.</summary>
    [Column("provider_id")]
    public Guid ProviderId { get; set; }

    /// <summary>The "sub" claim value from the OIDC provider's ID token.</summary>
    [Required, MaxLength(255), Column("oidc_subject_id")]
    public string OidcSubjectId { get; set; } = string.Empty;

    /// <summary>Token issuer URL, used to match incoming tokens to this identity.</summary>
    [Required, MaxLength(500), Column("issuer")]
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Email address from the OIDC provider, if available.</summary>
    [MaxLength(255), Column("email")]
    public string? Email { get; set; }

    /// <summary>
    /// The provider's refresh token for this identity, encrypted, kept only for a provider whose
    /// grant this instance must revoke when the identity goes (Sign in with Apple).
    /// </summary>
    [Column("provider_refresh_token")]
    public byte[]? ProviderRefreshTokenEncrypted { get; set; }

    /// <summary>When this OIDC identity was first linked to the subject.</summary>
    [Column("linked_at")]
    public DateTime LinkedAt { get; set; }

    /// <summary>Last time a login was completed with this identity.</summary>
    [Column("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    // Navigation properties

    /// <summary>The subject this identity belongs to.</summary>
    public SubjectEntity? Subject { get; set; }

    /// <summary>The OIDC provider configuration for this identity.</summary>
    public OidcProviderEntity? Provider { get; set; }
}
