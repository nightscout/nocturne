namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Manages tenant membership invite links: creation, acceptance, listing, and revocation.
/// Invites grant specified roles and optional direct permissions when accepted by a subject.
/// </summary>
/// <seealso cref="ITenantService"/>
/// <seealso cref="ITenantRoleService"/>
public interface IMemberInviteService
{
    /// <summary>
    /// Creates a new invite link that grants the specified roles and permissions when accepted.
    /// </summary>
    /// <param name="tenantId">The tenant the invite joins.</param>
    /// <param name="createdBySubjectId">Subject creating the invite.</param>
    /// <param name="granterPermissions">
    /// Permissions the creating caller holds. The invite's roles and direct permissions are
    /// validated against this set, so an invite cannot carry more access than its creator has.
    /// </param>
    /// <param name="roleIds">Roles the invite assigns; must belong to <paramref name="tenantId"/>.</param>
    /// <param name="directPermissions">Direct permission atoms the invite assigns.</param>
    /// <param name="label">Optional label recorded on the invite and the resulting membership.</param>
    /// <param name="expiresInDays">Days until the invite expires.</param>
    /// <param name="maxUses">Optional cap on the number of acceptances.</param>
    /// <param name="limitTo24Hours">Whether the resulting membership is clamped to 24 hours of data.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when no roles or permissions are supplied, a role does not belong to the tenant, or
    /// the grant exceeds <paramref name="granterPermissions"/>.
    /// </exception>
    Task<MemberInviteResult> CreateInviteAsync(
        Guid tenantId,
        Guid createdBySubjectId,
        IEnumerable<string> granterPermissions,
        List<Guid> roleIds,
        List<string>? directPermissions = null,
        string? label = null,
        int expiresInDays = 7,
        int? maxUses = null,
        bool limitTo24Hours = false);

    /// <summary>Retrieves invite details by token, or null if the token is invalid or expired.</summary>
    Task<MemberInviteInfo?> GetInviteByTokenAsync(string token);

    /// <summary>Accepts an invite and adds the subject as a member of the tenant.</summary>
    Task<AcceptMemberInviteResult> AcceptInviteAsync(string token, Guid acceptingSubjectId);

    /// <summary>Returns all invites for the specified tenant, including usage history.</summary>
    Task<List<MemberInviteInfo>> GetInvitesForTenantAsync(Guid tenantId);

    /// <summary>Revokes an invite so it can no longer be accepted.</summary>
    Task<bool> RevokeInviteAsync(Guid inviteId, Guid tenantId);
}

/// <summary>
/// Result returned when a new invite link is created via <see cref="IMemberInviteService.CreateInviteAsync"/>.
/// </summary>
/// <param name="Id">The unique identifier of the invite.</param>
/// <param name="Token">The opaque token embedded in the invite URL.</param>
/// <param name="InviteUrl">The full URL that the invitee should visit to accept.</param>
/// <param name="ExpiresAt">The UTC timestamp after which the invite is no longer valid.</param>
public record MemberInviteResult(
    Guid Id,
    string Token,
    string InviteUrl,
    DateTime ExpiresAt);

/// <summary>
/// Detailed view of an invite, including its current validity state and usage history.
/// </summary>
public record MemberInviteInfo(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string CreatedByName,
    List<Guid> RoleIds,
    List<string>? DirectPermissions,
    string? Label,
    bool LimitTo24Hours,
    DateTime ExpiresAt,
    int? MaxUses,
    int UseCount,
    bool IsValid,
    bool IsExpired,
    bool IsRevoked,
    DateTime CreatedAt,
    List<InviteUsageInfo> UsedBy);

/// <summary>
/// Records a single usage of an invite: which subject accepted it and when.
/// </summary>
public record InviteUsageInfo(
    Guid SubjectId,
    string? Name,
    DateTime JoinedAt);

/// <summary>
/// Result of accepting an invite via <see cref="IMemberInviteService.AcceptInviteAsync"/>.
/// On failure, <see cref="ErrorCode"/> and <see cref="ErrorDescription"/> describe the reason.
/// </summary>
public record AcceptMemberInviteResult(
    bool Success,
    string? ErrorCode = null,
    string? ErrorDescription = null,
    Guid? MembershipId = null);
