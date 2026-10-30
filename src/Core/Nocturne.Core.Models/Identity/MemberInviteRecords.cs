namespace Nocturne.Core.Models.Identity;

/// <summary>An invite to store, its token already hashed by the caller.</summary>
public sealed record NewMemberInvite(
    Guid TenantId,
    Guid CreatedBySubjectId,
    string TokenHash,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<string>? DirectPermissions,
    string? Label,
    bool LimitTo24Hours,
    DateTime ExpiresAt,
    int? MaxUses,
    DateTime CreatedAt);

/// <summary>A stored invite. The token hash stays in the store.</summary>
/// <param name="TenantDisplayName">Null when the tenant row was not read.</param>
/// <param name="CreatedByName">Null when the creator's row was not read, or is gone.</param>
/// <param name="Uses">The members who joined through it, when they were read.</param>
public sealed record MemberInviteRecord(
    Guid Id,
    Guid TenantId,
    string? TenantDisplayName,
    Guid CreatedBySubjectId,
    string? CreatedByName,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<string>? DirectPermissions,
    string? Label,
    bool LimitTo24Hours,
    DateTime ExpiresAt,
    int? MaxUses,
    int UseCount,
    DateTime? RevokedAt,
    DateTime CreatedAt,
    IReadOnlyList<MemberInviteUse> Uses)
{
    /// <summary>Whether the invite is expired at <paramref name="now"/>; the expiry instant itself is expired.</summary>
    public bool IsExpiredAt(DateTime now) => now >= ExpiresAt;

    public bool IsRevoked => RevokedAt.HasValue;

    public bool IsExhausted => MaxUses.HasValue && UseCount >= MaxUses.Value;

    /// <summary>Whether the invite can be accepted at <paramref name="now"/>.</summary>
    public bool IsValidAt(DateTime now) => !IsExpiredAt(now) && !IsRevoked && !IsExhausted;
}

/// <summary>A membership created through an invite.</summary>
public sealed record MemberInviteUse(Guid SubjectId, string? SubjectName, DateTime JoinedAt);

/// <summary>What revoking an invite found.</summary>
public enum MemberInviteRevocation
{
    NotFound,
    AlreadyRevoked,
    Revoked,
}
