namespace Nocturne.Core.Models.Identity;

/// <summary>A tenant creation invite as it is stored, before it has an id.</summary>
public sealed record NewTenantCreationInvite(
    Guid CreatedBySubjectId,
    string TokenHash,
    string? Label,
    DateTime ExpiresAt,
    int? MaxUses,
    DateTime CreatedAt);

/// <summary>
/// A platform admin's link that lets whoever holds it create an account and a tenant they own on
/// this instance, up to <see cref="MaxUses"/> times (no cap when null) before
/// <see cref="ExpiresAt"/>.
/// </summary>
public sealed record TenantCreationInviteRecord(
    Guid Id,
    Guid CreatedBySubjectId,
    string? CreatedByName,
    string? Label,
    DateTime ExpiresAt,
    int? MaxUses,
    int UseCount,
    DateTime? RevokedAt,
    DateTime CreatedAt,
    IReadOnlyList<TenantCreationInviteUse> Uses)
{
    /// <summary>Whether the link can still create a tenant at <paramref name="now"/>.</summary>
    public bool IsUsableAt(DateTime now) => Unusable(now) is null;

    /// <summary>
    /// Why the link can no longer create a tenant at <paramref name="now"/>, or null when it can.
    /// </summary>
    public TenantCreationInviteUnusable? Unusable(DateTime now) =>
        RevokedAt is not null ? TenantCreationInviteUnusable.Revoked
        : ExpiresAt <= now ? TenantCreationInviteUnusable.Expired
        : MaxUses is { } cap && UseCount >= cap ? TenantCreationInviteUnusable.UsedUp
        : null;
}

/// <summary>Why a tenant creation invite can no longer be used.</summary>
public enum TenantCreationInviteUnusable
{
    Revoked,
    Expired,
    UsedUp,
}

/// <summary>
/// One tenant a tenant creation invite created. The tenant fields are null once that tenant has
/// been deleted.
/// </summary>
public sealed record TenantCreationInviteUse(
    Guid? TenantId,
    string? TenantSlug,
    string? TenantDisplayName,
    DateTime RedeemedAt);
