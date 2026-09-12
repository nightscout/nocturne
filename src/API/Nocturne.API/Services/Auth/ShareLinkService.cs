using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Auth;
using Nocturne.API.Models.Responses;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Security;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages a tenant's single public share link: its token, the Public subject's read role, and
/// the 24-hour/full-history scope. Runs on the request-scoped <see cref="NocturneDbContext"/>;
/// the membership tables are not RLS-scoped, so tenant isolation comes from the explicit tenant-id
/// predicate on every query — those predicates must be preserved.
/// </summary>
public interface IShareLinkService
{
    /// <summary>
    /// Reports the link's state. <see cref="ShareLinkDto.Url"/> is always null; the redacted form
    /// and <see cref="ShareLinkDto.CanReveal"/> say whether <see cref="RevealAsync"/> can produce
    /// it. Keeping the secret out of this response keeps it off every render of the settings page.
    /// </summary>
    Task<ShareLinkDto> GetAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the live link's URL, decrypted from the stored ciphertext. The URL is null — with
    /// the rest of the state still reported — when nothing recoverable was kept for this link:
    /// it was minted before the token was stored recoverably, or the instance has no encryption
    /// key. Callers audit this; it hands out a credential.
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

    /// <summary>
    /// The appearance an anonymous share viewer renders the tenant's data with: the owner's
    /// display preferences, narrowed by <see cref="UserDisplayPreferences.ToPresentationOnly"/>.
    /// All-null when the tenant has no owner or the owner saved nothing, which leaves the viewer
    /// on the frontend's own defaults.
    /// </summary>
    Task<UserDisplayPreferences> GetSharedAppearanceAsync(Guid tenantId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ShareLinkService : IShareLinkService
{
    private const string PublicSubjectName = "Public";
    private const int MaxTokenAttempts = 5;

    private readonly NocturneDbContext _dbContext;
    private readonly IShareTokenGenerator _tokenGenerator;
    private readonly ShareTokenCacheService _shareTokenCache;
    private readonly PublicAccessCacheService _publicAccessCache;
    private readonly ISecretEncryptionService _secrets;
    private readonly ILogger<ShareLinkService> _logger;
    private readonly string _baseDomain;

    public ShareLinkService(
        NocturneDbContext dbContext,
        IShareTokenGenerator tokenGenerator,
        ShareTokenCacheService shareTokenCache,
        PublicAccessCacheService publicAccessCache,
        ISecretEncryptionService secrets,
        ILogger<ShareLinkService> logger,
        IOptions<BaseDomainOptions> baseDomain)
    {
        _dbContext = dbContext;
        _tokenGenerator = tokenGenerator;
        _shareTokenCache = shareTokenCache;
        _publicAccessCache = publicAccessCache;
        _secrets = secrets;
        _logger = logger;
        _baseDomain = baseDomain.Value.BaseDomain;
    }

    public async Task<ShareLinkDto> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await _dbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");

        var member = await _dbContext.TenantMembers.AsNoTracking()
            .Include(m => m.MemberRoles)
                .ThenInclude(mr => mr.TenantRole)
            .Include(m => m.Subject)
            .FirstOrDefaultAsync(m => m.TenantId == tenantId
                && m.Subject!.IsSystemSubject && m.Subject.Name == PublicSubjectName, ct);

        return ToDto(tenant, member);
    }

    public async Task<ShareLinkDto> RevealAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await _dbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");

        var member = await _dbContext.TenantMembers.AsNoTracking()
            .Include(m => m.MemberRoles)
                .ThenInclude(mr => mr.TenantRole)
            .Include(m => m.Subject)
            .FirstOrDefaultAsync(m => m.TenantId == tenantId
                && m.Subject!.IsSystemSubject && m.Subject.Name == PublicSubjectName, ct);

        return ToDto(tenant, member, DecryptToken(tenant));
    }

    public async Task<ShareLinkDto> RotateAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        var member = await GetPublicMemberAsync(tenantId, ct)
            ?? throw new InvalidOperationException("Public subject membership not found");

        var oldTokenHash = tenant.ShareToken;
        var wasEnabled = oldTokenHash != null;
        var newToken = await GenerateUniqueTokenAsync(ct);
        var now = DateTime.UtcNow;

        // On first enable, seed the default public scopes (glucose reads alone) as direct
        // permissions on the Public subject, and default to a 24-hour window. Re-rotation only
        // swaps the token — the owner's chosen scopes and window are preserved.
        if (!wasEnabled)
        {
            member.DirectPermissions = [.. Scope.DefaultPublicShareScopes];
            member.LimitTo24Hours = true;
        }

        tenant.ShareToken = CredentialHash.ShareToken(newToken);
        tenant.ShareTokenEncrypted = EncryptToken(newToken);
        tenant.ShareTokenSetAt = now;
        member.SysUpdatedAt = now;

        await _dbContext.SaveChangesAsync(ct);

        if (oldTokenHash != null)
            _shareTokenCache.EvictByHash(oldTokenHash);
        _publicAccessCache.Evict(tenantId);

        // The only moment the URL can be produced: the token itself is not stored.
        return ToDto(tenant, member, newToken);
    }

    public async Task<ShareLinkDto> DisableAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        var member = await GetPublicMemberAsync(tenantId, ct);
        var oldTokenHash = tenant.ShareToken;

        tenant.ShareToken = null;
        tenant.ShareTokenEncrypted = null;
        tenant.ShareTokenSetAt = null;

        if (member != null)
        {
            // Remove the Public subject's roles and scopes so anonymous read no longer resolves.
            _dbContext.TenantMemberRoles.RemoveRange(member.MemberRoles);
            member.MemberRoles.Clear();
            member.DirectPermissions = null;
            member.SysUpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(ct);

        if (oldTokenHash != null)
            _shareTokenCache.EvictByHash(oldTokenHash);
        _publicAccessCache.Evict(tenantId);

        return ToDto(tenant, member);
    }

    public async Task<ShareLinkDto> SetFullHistoryAsync(Guid tenantId, bool fullHistory, CancellationToken ct = default)
    {
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        var member = await GetPublicMemberAsync(tenantId, ct)
            ?? throw new InvalidOperationException("Public subject membership not found");

        member.LimitTo24Hours = !fullHistory;
        member.SysUpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(ct);
        _publicAccessCache.Evict(tenantId);

        return ToDto(tenant, member);
    }

    public async Task<ShareLinkDto> SetScopesAsync(Guid tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        var invalid = scopes.Where(s => !Scope.PublicShareScopes.Contains(s)).ToList();
        if (invalid.Count > 0)
            throw new ArgumentException($"Invalid public share scopes: {string.Join(", ", invalid)}", nameof(scopes));

        var tenant = await _dbContext.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        var member = await GetPublicMemberAsync(tenantId, ct)
            ?? throw new InvalidOperationException("Public subject membership not found");

        // Drop any role grant so the chosen scopes are authoritative. This also migrates legacy
        // links (which carried the Viewer role) onto the direct-permission model on first edit.
        if (member.MemberRoles.Count > 0)
        {
            _dbContext.TenantMemberRoles.RemoveRange(member.MemberRoles);
            member.MemberRoles.Clear();
        }

        member.DirectPermissions = scopes.Distinct().ToList();
        member.SysUpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(ct);
        _publicAccessCache.Evict(tenantId);

        return ToDto(tenant, member);
    }

    /// <inheritdoc />
    public async Task<UserDisplayPreferences> GetSharedAppearanceAsync(Guid tenantId, CancellationToken ct = default)
    {
        var ownerPreferences = await _dbContext.TenantMembers.AsNoTracking()
            .OwnersOf(tenantId)
            .Select(m => m.Subject!.Preferences)
            .FirstOrDefaultAsync(ct);

        return UserDisplayPreferences.Deserialize(ownerPreferences).ToPresentationOnly();
    }

    private Task<TenantMemberEntity?> GetPublicMemberAsync(Guid tenantId, CancellationToken ct) =>
        _dbContext.TenantMembers
            .Include(m => m.MemberRoles)
                .ThenInclude(mr => mr.TenantRole)
            .Include(m => m.Subject)
            .FirstOrDefaultAsync(m => m.TenantId == tenantId
                && m.Subject!.IsSystemSubject && m.Subject.Name == PublicSubjectName, ct);

    /// <summary>
    /// Mints a token whose digest is not already stored. Returns the token itself; the caller
    /// stores <see cref="CredentialHash.ShareToken"/> of it.
    /// </summary>
    private async Task<string> GenerateUniqueTokenAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxTokenAttempts; attempt++)
        {
            var candidate = _tokenGenerator.Generate();
            var candidateHash = CredentialHash.ShareToken(candidate);
            var exists = await _dbContext.Tenants.AnyAsync(t => t.ShareToken == candidateHash, ct);
            if (!exists)
                return candidate;
        }

        throw new InvalidOperationException("Unable to generate a unique share token after several attempts");
    }

    /// <summary>
    /// Projects the share link. <paramref name="token"/> is supplied by the paths entitled to hand
    /// the secret back — rotate, which has just minted it, and reveal, which has just decrypted it.
    /// Every other path reports only that a link exists, and in what shape.
    /// </summary>
    private ShareLinkDto ToDto(TenantEntity tenant, TenantMemberEntity? member, string? token = null)
    {
        var enabled = tenant.ShareToken != null;
        return new ShareLinkDto
        {
            Enabled = enabled,
            Url = token != null ? ShareUrl(token) : null,
            RedactedUrl = enabled ? ShareUrl(new string('•', ShareTokenGenerator.TokenLength)) : null,
            CanReveal = enabled && tenant.ShareTokenEncrypted != null && _secrets.IsConfigured,
            FullHistory = member is { LimitTo24Hours: false },
            Scopes = ComputeScopes(member),
            LastAccessedAt = tenant.ShareLastAccessedAt,
        };
    }

    private string ShareUrl(string token) => $"https://{token}.share.{_baseDomain}";

    /// <summary>
    /// Ciphertext of <paramref name="token"/>, or null on an instance with no key. A share link is
    /// not worth refusing to mint over — the tenant simply keeps the pre-existing behaviour of
    /// having to rotate to see one.
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
    /// The token behind <paramref name="tenant"/>'s live link, or null when none was kept or the
    /// ciphertext no longer decrypts — which is what a changed instance key looks like. The link
    /// itself still resolves in that case, since resolution reads the digest, so this is reported
    /// as "cannot reveal" rather than as a broken link.
    /// </summary>
    private string? DecryptToken(TenantEntity tenant)
    {
        if (tenant.ShareToken == null || tenant.ShareTokenEncrypted == null || !_secrets.IsConfigured)
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
    /// Requires <see cref="TenantMemberEntity.MemberRoles"/> (with their roles) to be loaded.
    /// </summary>
    private static List<string> ComputeScopes(TenantMemberEntity? member)
    {
        if (member == null)
            return [];

        var rolePermissions = member.MemberRoles
            .SelectMany(mr => mr.TenantRole?.Permissions ?? Enumerable.Empty<string>());
        var directPermissions = member.DirectPermissions ?? Enumerable.Empty<string>();

        return rolePermissions
            .Concat(directPermissions)
            .Where(Scope.PublicShareScopes.Contains)
            .Distinct()
            .ToList();
    }
}
