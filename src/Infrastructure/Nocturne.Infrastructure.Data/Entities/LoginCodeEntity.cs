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

    /// <summary>After this instant the code can no longer be exchanged.</summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// What the code can be exchanged for. A code redeems only at the endpoint for its purpose.
    /// </summary>
    [MaxLength(16)]
    [Column("purpose")]
    public LoginCodePurpose Purpose { get; set; } = LoginCodePurpose.Login;

    /// <summary>When the code was exchanged for a session; null until then.</summary>
    [Column("consumed_at")]
    public DateTime? ConsumedAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TenantEntity? Tenant { get; set; }

    public SubjectEntity? Subject { get; set; }
}

/// <summary>What a <see cref="LoginCodeEntity"/> can be exchanged for.</summary>
public enum LoginCodePurpose
{
    /// <summary>A full session, at <c>POST /api/auth/handoff</c>.</summary>
    Login,

    /// <summary>
    /// One passkey enrolment for a member with no sign-in method left, at
    /// <c>POST /api/auth/passkey/recovery-mode/redeem</c>, issued by one of the tenant's member
    /// administrators. Redeemable only while the member is still one a member administrator may
    /// recover.
    /// </summary>
    Recovery,

    /// <summary>
    /// As <see cref="Recovery"/>, issued by the operator, who may also recover an instance
    /// administrator or a member of several tenants.
    /// </summary>
    OperatorRecovery,
}
