using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Registered or pinned OAuth client applications.
/// Any app can initiate an OAuth flow without prior registration;
/// this table records clients that have been authorized at least once.
/// </summary>
[Table("oauth_clients")]
public class OAuthClientEntity
{
    /// <summary>
    /// Primary key - UUID Version 7
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The identifier the app presents (e.g., "xdrip-pixel9", redirect URI, or a well-known string)
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Column("client_id")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Display name set by tenant or from known app directory
    /// </summary>
    [MaxLength(255)]
    [Column("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether this client matched the bundled known app directory at authorization time
    /// </summary>
    [Column("is_known")]
    public bool IsKnown { get; set; }

    /// <summary>
    /// Validated redirect URIs for the authorization code flow (JSON array)
    /// </summary>
    [Column("redirect_uris")]
    public string RedirectUris { get; set; } = "[]";

    /// <summary>
    /// When this client was first registered
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this client was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties

    /// <summary>
    /// Grants issued to this client
    /// </summary>
    public ICollection<OAuthGrantEntity> Grants { get; set; } = new List<OAuthGrantEntity>();
}
