using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A tenant that holds a Google Health refresh token, and the account it was issued for
/// (<see cref="Nocturne.Core.Contracts.Health.IGoogleHealthGrantHolders"/>).
/// </summary>
/// <remarks>Not tenant-scoped: whether a grant may be revoked depends on every tenant's row.</remarks>
[Table("google_health_grant_holders")]
public class GoogleHealthGrantHolderEntity
{
    [Key]
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Hex SHA-256 of the Google account's subject, or null once the tenant holds no token. A row
    /// with no account still records the tenant's state, so adopting stored tokens leaves it alone.
    /// </summary>
    [Column("account_key")]
    [MaxLength(64)]
    public string? AccountKey { get; set; }
}
