using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Registered or pinned OAuth client applications.
/// Any app can initiate an OAuth flow without prior registration;
/// this table records clients that have been authorized at least once.
/// </summary>
[Table("oauth_clients")]
public class OAuthClientEntity : ITenantScoped, IEntityTimestamped
{
    /// <summary>
    /// Primary key - UUID Version 7
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Tenant that owns this client. OAuth clients are tenant-scoped so a client_id
    /// issued on one tenant subdomain is never valid on another.
    /// </summary>
    [Required]
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// The identifier the app presents (e.g., "xdrip-pixel9", redirect URI, or a well-known string)
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Column("client_id")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// RFC 7591 software_id — reverse-DNS identifier that is stable across installs of the
    /// same product (e.g., "org.trio.diabetes"). Used to match self-registering clients
    /// against the bundled known app directory for idempotent DCR.
    /// </summary>
    [MaxLength(255)]
    [Column("software_id")]
    public string? SoftwareId { get; set; }

    /// <summary>
    /// RFC 7591 client_name supplied during Dynamic Client Registration.
    /// </summary>
    [MaxLength(255)]
    [Column("client_name")]
    public string? ClientName { get; set; }

    /// <summary>
    /// RFC 7591 client_uri — homepage of the client application.
    /// </summary>
    [MaxLength(2048)]
    [Column("client_uri")]
    public string? ClientUri { get; set; }

    /// <summary>
    /// RFC 7591 logo_uri — logo of the client application for the consent screen.
    /// </summary>
    [MaxLength(2048)]
    [Column("logo_uri")]
    public string? LogoUri { get; set; }

    /// <summary>
    /// IP address that performed the registration (for abuse investigation).
    /// Stored as a string to accommodate both IPv4 and IPv6.
    /// </summary>
    [MaxLength(45)]
    [Column("created_from_ip")]
    public string? CreatedFromIp { get; set; }

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
