using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Auth;
using Nocturne.API.Models.Responses;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Security;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// The one member of <see cref="IShareLinkService"/> a share or guest viewer is allowed to reach, and
/// the reader of the owner's display preferences for any caller that needs the tenant's units.
/// </summary>
/// <remarks>
/// Separate so that <c>TenantAppearanceController</c>, which answers share and guest viewers,
/// does not depend on an interface carrying <see cref="IShareLinkService.RevealAsync"/>.
/// Nothing routes an anonymous caller there today; the narrower seam is what keeps that true when
/// someone adds the next member.
/// </remarks>
public interface ITenantAppearanceReader
{
    /// <summary>
    /// The appearance a share or guest viewer renders the tenant's data with: the longest-standing
    /// owner's display preferences, narrowed by <see cref="UserDisplayPreferences.ToPresentationOnly"/>.
    /// All-null when the tenant has no owner or the owner saved nothing, which leaves the viewer
    /// on the frontend's own defaults.
    /// </summary>
    Task<UserDisplayPreferences> GetTenantAppearanceAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// Manages a tenant's single public share link: its token, the Public subject's read role, and
/// the 24-hour/full-history scope. The membership tables are not RLS-scoped, so tenant isolation
/// comes from the explicit tenant-id predicate on every query; those predicates must be preserved.
/// </summary>
public interface IShareLinkService : ITenantAppearanceReader
{
    /// <summary>
    /// Reports the link's state. <see cref="ShareLinkDto.Url"/> is always null, so the secret
    /// stays off every render of the settings page.
    /// </summary>
    Task<ShareLinkDto> GetAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the live link's URL, decrypted from the stored ciphertext, leaving the URL null and
    /// <see cref="ShareLinkDto.CanReveal"/> false in the cases
    /// <see cref="TenantRecord.ShareTokenEncrypted"/> lists. Callers audit this; it hands out a
    /// credential.
    /// </summary>
    Task<ShareLinkDto> RevealAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Mints a new token, replacing any existing one, and returns the resulting
    /// <see cref="ShareLinkDto.Url"/>. The previous link stops resolving immediately.
    /// </summary>
    Task<ShareLinkDto> RotateAsync(Guid tenantId, CancellationToken ct = default);
    Task<ShareLinkDto> DisableAsync(Guid tenantId, CancellationToken ct = default);
    Task<ShareLinkDto> SetFullHistoryAsync(Guid tenantId, bool fullHistory, CancellationToken ct = default);

    /// <summary>
    /// Replace the data categories anonymous viewers can see. <paramref name="scopes"/> must be a
    /// subset of <see cref="Scope.PublicShareScopes"/>; an empty list leaves the link
    /// live but shares nothing. Any role grant on the Public subject is dropped so these scopes are
    /// authoritative.
    /// </summary>
    Task<ShareLinkDto> SetScopesAsync(Guid tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default);
}

/// <inheritdoc />
/// <remarks>
/// Each write edits the tenant's token columns and the Public membership together, on one
/// <see cref="DbUnitOfWork"/>, so a rotate or disable cannot leave a link resolving onto grants
/// it should not have, or the other way round.
/// </remarks>
public sealed class ShareLinkService : IShareLinkService
{
    private const int MaxTokenAttempts = 5;

    private readonly INocturneDbContextFactory _contexts;
    private readonly ITenantRepository _tenants;
    private readonly ITenantMembershipRepository _members;
    private readonly IShareTokenGenerator _tokenGenerator;
    private readonly ShareTokenCacheService _shareTokenCache;
    private readonly PublicAccessCacheService _publicAccessCache;
    private readonly ISecretEncryptionService _secrets;
    private readonly ILogger<ShareLinkService> _logger;
    private readonly string _baseDomain;

    public ShareLinkService(
        INocturneDbContextFactory contexts,
        ITenantRepository tenants,
        ITenantMembershipRepository members,
        IShareTokenGenerator tokenGenerator,
        ShareTokenCacheService shareTokenCache,
        PublicAccessCacheService publicAccessCache,
        ISecretEncryptionService secrets,
        ILogger<ShareLinkService> logger,
        IOptions<BaseDomainOptions> baseDomain)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _members = members ?? throw new ArgumentNullException(nameof(members));
        _tokenGenerator = tokenGenerator;
        _shareTokenCache = shareTokenCache;
        _publicAccessCache = publicAccessCache;
        _secrets = secrets;
        _logger = logger;
        _baseDomain = baseDomain.Value.BaseDomain;
    }

    public async Task<ShareLinkDto> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var (tenant, member) = await ReadAsync(_tenants, _members, tenantId, ct);
        return ToDto(tenant, member);
    }

    public async Task<ShareLinkDto> RevealAsync(Guid tenantId, CancellationToken ct = default)
    {
        var (tenant, member) = await ReadAsync(_tenants, _members, tenantId, ct);
        return ToDto(tenant, member, DecryptToken(tenant), attemptedDecrypt: true);
    }

    /// <summary>The tenant and its Public membership.</summary>
    private static async Task<(TenantRecord Tenant, TenantMemberRecord? Member)> ReadAsync(
        ITenantRepository tenants, ITenantMembershipRepository members, Guid tenantId, CancellationToken ct)
    {
        var tenant = await tenants.GetAsync(tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        var member = await members.FindPublicMemberAsync(tenantId, ct);
        return (tenant, member);
    }

    public async Task<ShareLinkDto> RotateAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(_contexts, ct);
        var tenants = new TenantRepository(unit);
        var members = new TenantMembershipRepository(unit);
        var (tenant, member) = await ReadAsync(tenants, members, tenantId, ct);
        if (member is null)
            throw new InvalidOperationException("Public subject membership not found");

        var oldTokenHash = tenant.ShareTokenHash;
        var wasEnabled = oldTokenHash != null;
        var newToken = await GenerateUniqueTokenAsync(tenants, ct);
        var now = DateTime.UtcNow;

        // On first enable, seed the default public scopes (glucose reads alone) as direct
        // permissions on the Public subject, and default to a 24-hour window. Re-rotation only
        // swaps the token — the owner's chosen scopes and window are preserved.
        var permissions = wasEnabled ? member.DirectPermissions : [.. Scope.DefaultPublicShareScopes];
        var limitTo24Hours = wasEnabled ? member.LimitTo24Hours : true;
        var token = new ShareTokenState(CredentialHash.ShareToken(newToken), EncryptToken(newToken), now);

        await unit.CommitAsync(async t =>
        {
            await tenants.SetShareTokenAsync(tenantId, token, t);
            await members.SetDirectPermissionsAsync(tenantId, member.Id, permissions, now, t);
            await members.SetLimitTo24HoursAsync(tenantId, member.Id, limitTo24Hours, now, t);
            await unit.Context.SaveChangesAsync(t);
        }, ct);

        if (oldTokenHash != null)
            _shareTokenCache.EvictByHash(oldTokenHash);
        _publicAccessCache.Evict(tenantId);

        return ToDto(
            tenant with { ShareTokenHash = token.Hash, ShareTokenEncrypted = token.Encrypted, ShareTokenSetAt = now },
            member with { DirectPermissions = permissions, LimitTo24Hours = limitTo24Hours },
            newToken);
    }

    public async Task<ShareLinkDto> DisableAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(_contexts, ct);
        var tenants = new TenantRepository(unit);
        var members = new TenantMembershipRepository(unit);
        var (tenant, member) = await ReadAsync(tenants, members, tenantId, ct);
        var oldTokenHash = tenant.ShareTokenHash;
        var now = DateTime.UtcNow;

        await unit.CommitAsync(async t =>
        {
            await tenants.SetShareTokenAsync(tenantId, ShareTokenState.Off, t);
            if (member != null)
            {
                // Remove the Public subject's roles and scopes so anonymous read no longer resolves.
                await members.ReplaceRolesAsync(tenantId, member.Id, member.Roles.Select(r => r.RoleId).ToList(), [], now, t);
                await members.SetDirectPermissionsAsync(tenantId, member.Id, null, now, t);
            }
            await unit.Context.SaveChangesAsync(t);
        }, ct);

        if (oldTokenHash != null)
            _shareTokenCache.EvictByHash(oldTokenHash);
        _publicAccessCache.Evict(tenantId);

        return ToDto(
            tenant with { ShareTokenHash = null, ShareTokenEncrypted = null, ShareTokenSetAt = null },
            member is null ? null : member with { Roles = [], DirectPermissions = null });
    }

    public async Task<ShareLinkDto> SetFullHistoryAsync(Guid tenantId, bool fullHistory, CancellationToken ct = default)
    {
        var (tenant, member) = await ReadAsync(_tenants, _members, tenantId, ct);
        if (member is null)
            throw new InvalidOperationException("Public subject membership not found");

        await _members.SetLimitTo24HoursAsync(tenantId, member.Id, !fullHistory, DateTime.UtcNow, ct);
        _publicAccessCache.Evict(tenantId);

        return ToDto(tenant, member with { LimitTo24Hours = !fullHistory });
    }

    public async Task<ShareLinkDto> SetScopesAsync(Guid tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        var invalid = scopes.Where(s => !Scope.PublicShareScopes.Contains(s)).ToList();
        if (invalid.Count > 0)
            throw new ArgumentException($"Invalid public share scopes: {string.Join(", ", invalid)}", nameof(scopes));

        await using var unit = await DbUnitOfWork.OpenAsync(_contexts, ct);
        var tenants = new TenantRepository(unit);
        var members = new TenantMembershipRepository(unit);
        var (tenant, member) = await ReadAsync(tenants, members, tenantId, ct);
        if (member is null)
            throw new InvalidOperationException("Public subject membership not found");

        var permissions = scopes.Distinct().ToList();
        var now = DateTime.UtcNow;

        await unit.CommitAsync(async t =>
        {
            // Drop any role grant so the chosen scopes are authoritative. This also migrates legacy
            // links (which carried the Viewer role) onto the direct-permission model on first edit.
            if (member.Roles.Count > 0)
                await members.ReplaceRolesAsync(tenantId, member.Id, member.Roles.Select(r => r.RoleId).ToList(), [], now, t);
            await members.SetDirectPermissionsAsync(tenantId, member.Id, permissions, now, t);
            await unit.Context.SaveChangesAsync(t);
        }, ct);

        _publicAccessCache.Evict(tenantId);

        return ToDto(tenant, member with { Roles = [], DirectPermissions = permissions });
    }

    /// <inheritdoc />
    public async Task<UserDisplayPreferences> GetTenantAppearanceAsync(Guid tenantId, CancellationToken ct = default)
    {
        var ownerPreferences = await _members.GetEarliestOwnerPreferencesAsync(tenantId, ct);
        return UserDisplayPreferences.Deserialize(ownerPreferences).ToPresentationOnly();
    }

    /// <summary>
    /// Mints a token whose digest is not already stored. Returns the token itself; the caller
    /// stores <see cref="CredentialHash.ShareToken"/> of it.
    /// </summary>
    private async Task<string> GenerateUniqueTokenAsync(ITenantRepository tenants, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxTokenAttempts; attempt++)
        {
            var candidate = _tokenGenerator.Generate();
            if (!await tenants.ShareTokenHashExistsAsync(CredentialHash.ShareToken(candidate), ct))
                return candidate;
        }

        throw new InvalidOperationException("Unable to generate a unique share token after several attempts");
    }

    /// <summary>
    /// Projects the share link. <paramref name="token"/> is supplied by the paths entitled to hand
    /// the secret back: rotate, which has just minted it, and reveal, which has just decrypted it.
    /// Every other path reports only that a link exists, and in what shape.
    /// </summary>
    /// <remarks>
    /// <paramref name="attemptedDecrypt"/> is true on the reveal path, where
    /// <paramref name="token"/> is the outcome of an actual decrypt and so a null one settles
    /// <see cref="ShareLinkDto.CanReveal"/>. Off the reveal path the columns are all there is to go
    /// on, and they cannot see the changed-key case <see cref="TenantRecord.ShareTokenEncrypted"/>
    /// describes.
    /// </remarks>
    private ShareLinkDto ToDto(
        TenantRecord tenant,
        TenantMemberRecord? member,
        string? token = null,
        bool attemptedDecrypt = false)
    {
        var enabled = tenant.ShareTokenHash != null;
        return new ShareLinkDto
        {
            Enabled = enabled,
            Url = token != null ? ShareUrl(token) : null,
            RedactedUrl = enabled ? ShareUrl(new string('•', ShareTokenGenerator.TokenLength)) : null,
            CanReveal = attemptedDecrypt
                ? token != null
                : enabled && tenant.ShareTokenEncrypted != null && _secrets.IsConfigured,
            FullHistory = member is { LimitTo24Hours: false },
            Scopes = ComputeScopes(member),
            LastAccessedAt = tenant.ShareLastAccessedAt,
        };
    }

    private string ShareUrl(string token) => $"https://{token}.share.{_baseDomain}";

    /// <summary>
    /// Ciphertext of <paramref name="token"/>, or null when it could not be produced. Minting is
    /// not refused over it; see <see cref="TenantRecord.ShareTokenEncrypted"/>.
    /// </summary>
    private string? EncryptToken(string token)
    {
        if (!_secrets.IsConfigured)
            return null;

        try
        {
            return _secrets.Encrypt(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not keep a recoverable copy of the share token; the owner will have to rotate to see the link");
            return null;
        }
    }

    /// <summary>
    /// The token behind <paramref name="tenant"/>'s live link, or null in any of the cases
    /// <see cref="TenantRecord.ShareTokenEncrypted"/> lists. This is the only place the third of
    /// them is detectable.
    /// </summary>
    private string? DecryptToken(TenantRecord tenant)
    {
        if (tenant.ShareTokenHash == null || tenant.ShareTokenEncrypted == null || !_secrets.IsConfigured)
            return null;

        try
        {
            return _secrets.Decrypt(tenant.ShareTokenEncrypted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stored share token did not decrypt for tenant {TenantId}", tenant.Id);
            return null;
        }
    }

    /// <summary>
    /// The public-shareable read scopes the Public subject currently resolves to — the union of any
    /// role-granted permissions and direct permissions, narrowed to <see cref="Scope.PublicShareScopes"/>.
    /// </summary>
    private static List<string> ComputeScopes(TenantMemberRecord? member)
    {
        if (member == null)
            return [];

        return member.EffectivePermissions()
            .Where(scope => Scope.PublicShareScopes.Contains(scope)
                            && Scope.SharesWithPublic(scope, member.DirectPermissions))
            .ToList();
    }
}
