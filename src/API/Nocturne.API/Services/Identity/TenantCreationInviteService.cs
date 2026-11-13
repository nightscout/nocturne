using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Services.Identity;

/// <inheritdoc cref="ITenantCreationInviteService"/>
public class TenantCreationInviteService(
    ITenantCreationInviteRepository invites,
    ITenantRepository tenants,
    ITenantService tenantService,
    IJwtService jwtService,
    IOptions<BaseDomainOptions> baseDomain,
    TimeProvider time,
    ILogger<TenantCreationInviteService> logger) : ITenantCreationInviteService
{
    public const int MaxExpiresInDays = 90;
    public const int MaxUsesCeiling = 1000;
    private const int MaxLabelLength = 255;
    private const int MaxDisplayNameLength = 256;

    private BaseDomainOptions Domain => baseDomain.Value;

    /// <inheritdoc/>
    public async Task<TenantCreationInviteOverview> GetOverviewAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var all = await invites.ListAsync(ct);
        return new TenantCreationInviteOverview(
            all.Select(i => ToDto(i, now)).ToList(),
            string.IsNullOrEmpty(Domain.BaseDomain) ? null : Domain.BaseDomain,
            await SubdomainNoticeAsync(ct));
    }

    /// <inheritdoc/>
    public async Task<TenantInviteOutcome<TenantCreationInviteCreated>> CreateAsync(
        Guid createdBySubjectId, CreateTenantCreationInviteRequest request, CancellationToken ct)
    {
        if (Domain.PublicOrigin is not { } origin)
        {
            return TenantInviteOutcome<TenantCreationInviteCreated>.Refused(
                TenantInviteRefusalCode.BaseDomainRequired,
                "Set BASE_DOMAIN before you invite people: each new tenant needs its own subdomain.");
        }

        if (request.ExpiresInDays is < 1 or > MaxExpiresInDays)
        {
            return TenantInviteOutcome<TenantCreationInviteCreated>.Refused(
                TenantInviteRefusalCode.InvalidRequest, $"Expiry must be between 1 and {MaxExpiresInDays} days.");
        }

        if (request.MaxUses is < 1 or > MaxUsesCeiling)
        {
            return TenantInviteOutcome<TenantCreationInviteCreated>.Refused(
                TenantInviteRefusalCode.InvalidRequest, $"The number of uses must be between 1 and {MaxUsesCeiling}.");
        }

        var label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        if (label is { Length: > MaxLabelLength })
        {
            return TenantInviteOutcome<TenantCreationInviteCreated>.Refused(
                TenantInviteRefusalCode.InvalidRequest, $"The label must be {MaxLabelLength} characters or fewer.");
        }

        if (!request.ConfirmSubdomainChange && await SubdomainNoticeAsync(ct) is not null)
        {
            return TenantInviteOutcome<TenantCreationInviteCreated>.Refused(
                TenantInviteRefusalCode.SubdomainChangeUnconfirmed,
                "Confirm that every app uses the tenant's subdomain before you make the first link.");
        }

        var token = jwtService.GenerateRefreshToken();
        var tokenHash = HashUtils.Sha256Hex(token);
        var now = time.GetUtcNow().UtcDateTime;
        var expiresAt = now.AddDays(request.ExpiresInDays);
        var id = await invites.CreateAsync(
            new NewTenantCreationInvite(createdBySubjectId, tokenHash, label, expiresAt, request.MaxUses, now),
            CancellationToken.None);

        logger.LogInformation(
            "TenantCreationInviteAudit: {Event} invite_id={InviteId} created_by={SubjectId} max_uses={MaxUses} expires_at={ExpiresAt}",
            "invite_created", id, createdBySubjectId, request.MaxUses, expiresAt);

        var record = (await invites.FindByTokenHashAsync(tokenHash, CancellationToken.None))!;
        var url =
            $"{origin}{ITenantCreationInviteService.StartPath}?{ITenantCreationInviteService.TokenQueryParameter}={Uri.EscapeDataString(token)}";

        return TenantInviteOutcome<TenantCreationInviteCreated>.Ok(
            new TenantCreationInviteCreated(ToDto(record, now), url));
    }

    /// <inheritdoc/>
    public async Task<bool> RevokeAsync(Guid inviteId, CancellationToken ct)
    {
        var revoked = await invites.RevokeAsync(inviteId, time.GetUtcNow().UtcDateTime, ct);
        if (revoked)
        {
            logger.LogInformation(
                "TenantCreationInviteAudit: {Event} invite_id={InviteId}", "invite_revoked", inviteId);
        }

        return revoked;
    }

    /// <inheritdoc/>
    public async Task<TenantInviteOutcome<TenantCreationInviteInfo>> GetInfoAsync(string token, CancellationToken ct)
    {
        var (invite, refusal) = await FindUsableAsync(token, ct);
        if (refusal is not null)
            return new TenantInviteOutcome<TenantCreationInviteInfo>(null, refusal);

        return TenantInviteOutcome<TenantCreationInviteInfo>.Ok(
            new TenantCreationInviteInfo(Domain.BaseDomain, invite!.CreatedByName, invite.ExpiresAt));
    }

    /// <inheritdoc/>
    public async Task<TenantInviteOutcome<Guid>> ClaimAsync(string token, CancellationToken ct)
    {
        var (invite, refusal) = await FindUsableAsync(token, ct);
        if (refusal is not null)
            return new TenantInviteOutcome<Guid>(default, refusal);

        if (!await invites.TryClaimUseAsync(invite!.Id, time.GetUtcNow().UtcDateTime, ct))
        {
            return TenantInviteOutcome<Guid>.Refused(
                TenantInviteRefusalCode.UsedUp, "This invite link was used by someone else first.");
        }

        return TenantInviteOutcome<Guid>.Ok(invite.Id);
    }

    /// <inheritdoc/>
    public Task ReleaseAsync(Guid inviteId) => invites.ReleaseUseAsync(inviteId, CancellationToken.None);

    /// <inheritdoc/>
    public async Task<TenantInviteOutcome<TenantCreationInviteRedeemed>> CreateTenantAsync(
        Guid claimedInviteId, string slug, string displayName, Guid ownerSubjectId, string? ownerUsername,
        CancellationToken ct)
    {
        // The use is already taken, so nothing past this point may be cancelled by the caller: a
        // disconnect would leave the link spent with no tenant. Every exit that creates no tenant
        // gives the use back.
        TenantCreatedDto? created = null;
        try
        {
            var name = displayName.Trim();
            if (name.Length is 0 or > MaxDisplayNameLength)
            {
                return TenantInviteOutcome<TenantCreationInviteRedeemed>.Refused(
                    TenantInviteRefusalCode.InvalidRequest,
                    $"The site name must be 1 to {MaxDisplayNameLength} characters.");
            }

            var validation = await tenantService.ValidateSlugAsync(slug, CancellationToken.None);
            if (!validation.IsValid)
            {
                return TenantInviteOutcome<TenantCreationInviteRedeemed>.Refused(
                    TenantInviteRefusalCode.InvalidSlug, validation.Message ?? "This subdomain cannot be used.");
            }

            try
            {
                created = await tenantService.CreateWithOwnerAsync(
                    slug, name, ownerSubjectId, ownerUsername, CancellationToken.None);
            }
            catch (DbUpdateException)
            {
                // The tenant and its owner are one transaction, so a failed create leaves no row of
                // its own: a slug that exists now was taken by a concurrent create.
                if (!await tenants.SlugExistsAsync(slug.ToLowerInvariant().Trim(), CancellationToken.None))
                    throw;

                return TenantInviteOutcome<TenantCreationInviteRedeemed>.Refused(
                    TenantInviteRefusalCode.InvalidSlug, "This name is already taken");
            }
        }
        finally
        {
            if (created is null)
                await ReleaseAsync(claimedInviteId);
        }

        await invites.RecordUseAsync(
            claimedInviteId, created.Id, time.GetUtcNow().UtcDateTime, CancellationToken.None);

        logger.LogInformation(
            "TenantCreationInviteAudit: {Event} invite_id={InviteId} tenant_id={TenantId} owner={SubjectId}",
            "invite_redeemed", claimedInviteId, created.Id, ownerSubjectId);

        return TenantInviteOutcome<TenantCreationInviteRedeemed>.Ok(
            new TenantCreationInviteRedeemed(
                created.Id, created.Slug, created.DisplayName, Domain.TenantOrigin(created.Slug) ?? ""));
    }

    private async Task<(TenantCreationInviteRecord? Invite, TenantInviteRefusal? Refusal)> FindUsableAsync(
        string token, CancellationToken ct)
    {
        var invite = string.IsNullOrEmpty(token)
            ? null
            : await invites.FindByTokenHashAsync(HashUtils.Sha256Hex(token), ct);

        if (invite is null)
            return (null, new TenantInviteRefusal(TenantInviteRefusalCode.NotFound, "This invite link is not valid."));

        return invite.Unusable(time.GetUtcNow().UtcDateTime) switch
        {
            TenantCreationInviteUnusable.Revoked => (null, new TenantInviteRefusal(
                TenantInviteRefusalCode.Revoked, "This invite link was cancelled.")),
            TenantCreationInviteUnusable.Expired => (null, new TenantInviteRefusal(
                TenantInviteRefusalCode.Expired, "This invite link has expired.")),
            TenantCreationInviteUnusable.UsedUp => (null, new TenantInviteRefusal(
                TenantInviteRefusalCode.UsedUp, "This invite link was already used.")),
            _ => (invite, null),
        };
    }

    private async Task<SubdomainNotice?> SubdomainNoticeAsync(CancellationToken ct)
    {
        if (Domain.PublicOrigin is not { } bare)
            return null;

        var sole = await tenants.FindSoleTenantAsync(ct);
        return sole is null
            ? null
            : new SubdomainNotice(sole.Slug, sole.DisplayName, bare, Domain.TenantOrigin(sole.Slug)!);
    }

    private TenantCreationInviteDto ToDto(TenantCreationInviteRecord invite, DateTime now) =>
        new(
            invite.Id,
            invite.Label,
            invite.CreatedByName,
            invite.ExpiresAt,
            invite.MaxUses,
            invite.UseCount,
            invite.RevokedAt,
            invite.CreatedAt,
            invite.Unusable(now) switch
            {
                TenantCreationInviteUnusable.Revoked => TenantCreationInviteStatus.Revoked,
                TenantCreationInviteUnusable.Expired => TenantCreationInviteStatus.Expired,
                TenantCreationInviteUnusable.UsedUp => TenantCreationInviteStatus.UsedUp,
                _ => TenantCreationInviteStatus.Active,
            },
            invite.Uses
                .Select(u => new TenantCreationInviteUseDto(
                    u.TenantSlug,
                    u.TenantDisplayName,
                    u.TenantSlug is null ? null : Domain.TenantOrigin(u.TenantSlug),
                    u.RedeemedAt))
                .ToList());
}
