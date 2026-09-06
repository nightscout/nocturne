using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A short-lived, single-use code that a platform administrator mints for a tenant member so the
/// browser it is handed to can be signed in as that member without a second credential ceremony.
/// The code itself is never stored; only its SHA-256 hash is persisted.
/// </summary>
/// <remarks>
/// Tenant-scoped for the same reason as <see cref="MemberInviteEntity"/>: the row is a bearer
/// credential for exactly one tenant, so the global query filter and the <c>tenant_isolation</c>
/// RLS policy are what stop a code minted on one tenant from being exchanged on another's host.
/// </remarks>
[Table("login_codes")]
public class LoginCodeEntity : ITenantScoped, IEntityCreated
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The tenant the resulting session belongs to.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>The member the resulting session is issued to.</summary>
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>SHA-256 hash of the opaque code.</summary>
    [Required]
    [MaxLength(64)]
    [Column("code_hash")]
    public string CodeHash { get; set; } = string.Empty;

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the code was exchanged for a session; null until then.</summary>
    [Column("consumed_at")]
    public DateTime? ConsumedAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TenantEntity? Tenant { get; set; }

    public SubjectEntity? Subject { get; set; }
}
