using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Tenant creation invites: links a platform admin hands out so that someone without an account
/// can create one and a tenant they own on this instance.
/// </summary>
/// <remarks>
/// A link is redeemed in two steps around whatever proves who the new owner is. <see cref="ClaimAsync"/>
/// takes one of the link's uses before the ceremony; <see cref="CreateTenantAsync"/> then creates the
/// tenant and records the use, or gives the use back when it cannot. A caller whose ceremony fails
/// between the two gives it back with <see cref="ReleaseAsync"/>.
/// </remarks>
public interface ITenantCreationInviteService
{
    /// <summary>Path of the web page a link opens, on the instance's bare domain.</summary>
    public const string StartPath = "/start";

    /// <summary>Query parameter carrying the token on <see cref="StartPath"/>.</summary>
    public const string TokenQueryParameter = "token";

    /// <summary>Every link, and what the inviter has to know before the next one is made.</summary>
    Task<TenantCreationInviteOverview> GetOverviewAsync(CancellationToken ct);

    /// <summary>
    /// Makes a link. While the bare domain serves a sole tenant, refuses unless
    /// <see cref="CreateTenantCreationInviteRequest.ConfirmSubdomainChange"/> is set, since the
    /// first tenant the link creates stops the bare domain serving it.
    /// </summary>
    Task<TenantInviteOutcome<TenantCreationInviteCreated>> CreateAsync(
        Guid createdBySubjectId, CreateTenantCreationInviteRequest request, CancellationToken ct);

    /// <summary>Revokes a link. False when no link has that id.</summary>
    Task<bool> RevokeAsync(Guid inviteId, CancellationToken ct);

    /// <summary>What the holder of <paramref name="token"/> is shown, or why the link is unusable.</summary>
    Task<TenantInviteOutcome<TenantCreationInviteInfo>> GetInfoAsync(string token, CancellationToken ct);

    /// <summary>Takes one use of the link, returning its id, or why it could not be taken.</summary>
    Task<TenantInviteOutcome<Guid>> ClaimAsync(string token, CancellationToken ct);

    /// <summary>Gives back a use <see cref="ClaimAsync"/> took for a tenant that was never created.</summary>
    Task ReleaseAsync(Guid inviteId);

    /// <summary>
    /// Creates the tenant for a claimed use, with <paramref name="ownerSubjectId"/> as its owner
    /// under <paramref name="ownerUsername"/> when one is given, and records the use. Gives the use
    /// back when the tenant cannot be created.
    /// </summary>
    Task<TenantInviteOutcome<TenantCreationInviteRedeemed>> CreateTenantAsync(
        Guid claimedInviteId, string slug, string displayName, Guid ownerSubjectId, string? ownerUsername,
        CancellationToken ct);
}

/// <summary>A value, or the refusal that took its place.</summary>
public sealed record TenantInviteOutcome<T>(T? Value, TenantInviteRefusal? Refusal)
{
    public static TenantInviteOutcome<T> Ok(T value) => new(value, null);

    public static TenantInviteOutcome<T> Refused(TenantInviteRefusalCode code, string message) =>
        new(default, new TenantInviteRefusal(code, message));
}

/// <summary>Why a tenant creation invite request was refused.</summary>
public sealed record TenantInviteRefusal(TenantInviteRefusalCode Code, string Message);

/// <summary>The machine-readable reason behind a <see cref="TenantInviteRefusal"/>.</summary>
public enum TenantInviteRefusalCode
{
    NotFound,
    Revoked,
    Expired,
    UsedUp,
    InvalidRequest,
    InvalidSlug,
    BaseDomainRequired,
    SubdomainChangeUnconfirmed,
}

/// <summary>Where a tenant creation invite stands.</summary>
public enum TenantCreationInviteStatus
{
    Active,
    Revoked,
    Expired,
    UsedUp,
}

/// <param name="Label">A note for the inviter, such as who the link is for.</param>
/// <param name="MaxUses">How many tenants the link may create; null for no cap.</param>
/// <param name="ExpiresInDays">Days until the link stops working, 1 to 90.</param>
/// <param name="ConfirmSubdomainChange">
/// The inviter has read <see cref="TenantCreationInviteOverview.SubdomainNotice"/> and moved every
/// app off the bare domain. Required only while that notice is present.
/// </param>
public record CreateTenantCreationInviteRequest(
    string? Label,
    int? MaxUses,
    int ExpiresInDays,
    bool ConfirmSubdomainChange = false);

public record TenantCreationInviteDto(
    Guid Id,
    string? Label,
    string? CreatedByName,
    DateTime ExpiresAt,
    int? MaxUses,
    int UseCount,
    DateTime? RevokedAt,
    DateTime CreatedAt,
    TenantCreationInviteStatus Status,
    List<TenantCreationInviteUseDto> Uses);

/// <param name="Address">The tenant's origin, or null once the tenant has been deleted.</param>
public record TenantCreationInviteUseDto(string? Slug, string? DisplayName, string? Address, DateTime RedeemedAt);

/// <param name="Url">The link to hand out. Shown once: only its hash is stored.</param>
public record TenantCreationInviteCreated(TenantCreationInviteDto Invite, string Url);

/// <param name="BaseDomain">The instance's base domain, or null when none is configured.</param>
/// <param name="SubdomainNotice">
/// Present while the bare domain serves a sole tenant, which the next tenant created ends.
/// </param>
public record TenantCreationInviteOverview(
    List<TenantCreationInviteDto> Invites,
    string? BaseDomain,
    SubdomainNotice? SubdomainNotice);

/// <summary>
/// The bare domain serves <see cref="TenantDisplayName"/> while it is the only tenant. Once a second
/// tenant exists, requests to <see cref="BareAddress"/> resolve no tenant, so every app and person
/// using it has to use <see cref="TenantAddress"/> instead.
/// </summary>
public record SubdomainNotice(
    string TenantSlug,
    string TenantDisplayName,
    string BareAddress,
    string TenantAddress);

/// <summary>What the holder of a usable link is shown.</summary>
/// <param name="BaseDomain">The domain every new tenant's subdomain sits under.</param>
/// <param name="InvitedByName">The platform admin who made the link.</param>
public record TenantCreationInviteInfo(
    string BaseDomain,
    string? InvitedByName,
    DateTime ExpiresAt);

/// <param name="Address">The new tenant's origin.</param>
public record TenantCreationInviteRedeemed(Guid TenantId, string Slug, string DisplayName, string Address);
