using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.API.Models.Responses;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages a tenant's single public share link: its token, the Public subject's read role, and
/// the 24-hour/full-history scope. Runs on the request-scoped <see cref="NocturneDbContext"/>;
/// the membership tables are not RLS-scoped, so tenant isolation comes from the explicit tenant-id
/// predicate on every query — those predicates must be preserved.
/// </summary>
public interface IShareLinkService
{
    Task<ShareLinkDto> GetAsync(Guid tenantId, CancellationToken ct = default);
    Task<ShareLinkDto> RotateAsync(Guid tenantId, CancellationToken ct = default);
    Task<ShareLinkDto> DisableAsync(Guid tenantId, CancellationToken ct = default);
    Task<ShareLinkDto> SetFullHistoryAsync(Guid tenantId, bool fullHistory, CancellationToken ct = default);

    /// <summary>
    /// Replace the data categories anonymous viewers can see. <paramref name="scopes"/> must be a
    /// subset of <see cref="TenantPermissions.PublicShareScopes"/>; an empty list leaves the link
    /// live but shares nothing. Any role grant on the Public subject is dropped so these scopes are
    /// authoritative.
    /// </summary>
    Task<ShareLinkDto> SetScopesAsync(Guid tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default);
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
    private readonly string _baseDomain;

    public ShareLinkService(
        NocturneDbContext dbContext,
        IShareTokenGenerator tokenGenerator,
        ShareTokenCacheService shareTokenCache,
        PublicAccessCacheService publicAccessCache,
        IOptions<BaseDomainOptions> baseDomain)
    {
        _dbContext = dbContext;
        _tokenGenerator = tokenGenerator;
        _shareTokenCache = shareTokenCache;
        _publicAccessCache = publicAccessCache;
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

    public async Task<ShareLinkDto> RotateAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        var member = await GetPublicMemberAsync(tenantId, ct)
            ?? throw new InvalidOperationException("Public subject membership not found");

        var oldToken = tenant.ShareToken;
        var wasEnabled = oldToken != null;
        var newToken = await GenerateUniqueTokenAsync(ct);
        var now = DateTime.UtcNow;

        // On first enable, seed the default public scopes (glucose + statistics) as direct
        // permissions on the Public subject, and default to a 24-hour window. Re-rotation only
        // swaps the token — the owner's chosen scopes and window are preserved.
        if (!wasEnabled)
        {
            member.DirectPermissions = [.. TenantPermissions.DefaultPublicShareScopes];
            member.LimitTo24Hours = true;
        }

        tenant.ShareToken = newToken;
        tenant.ShareTokenSetAt = now;
        member.SysUpdatedAt = now;

        await _dbContext.SaveChangesAsync(ct);

        if (oldToken != null)
            _shareTokenCache.Evict(oldToken);
        _publicAccessCache.Evict(tenantId);

        return ToDto(tenant, member);
    }

    public async Task<ShareLinkDto> DisableAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        var member = await GetPublicMemberAsync(tenantId, ct);
        var oldToken = tenant.ShareToken;

        tenant.ShareToken = null;
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

        if (oldToken != null)
            _shareTokenCache.Evict(oldToken);
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
        var invalid = scopes.Where(s => !TenantPermissions.PublicShareScopes.Contains(s)).ToList();
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

    private Task<TenantMemberEntity?> GetPublicMemberAsync(Guid tenantId, CancellationToken ct) =>
        _dbContext.TenantMembers
            .Include(m => m.MemberRoles)
                .ThenInclude(mr => mr.TenantRole)
            .Include(m => m.Subject)
            .FirstOrDefaultAsync(m => m.TenantId == tenantId
                && m.Subject!.IsSystemSubject && m.Subject.Name == PublicSubjectName, ct);

    private async Task<string> GenerateUniqueTokenAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxTokenAttempts; attempt++)
        {
            var candidate = _tokenGenerator.Generate();
            var exists = await _dbContext.Tenants.AnyAsync(t => t.ShareToken == candidate, ct);
            if (!exists)
                return candidate;
        }

        throw new InvalidOperationException("Unable to generate a unique share token after several attempts");
    }

    private ShareLinkDto ToDto(TenantEntity tenant, TenantMemberEntity? member) => new()
    {
        Enabled = tenant.ShareToken != null,
        Url = tenant.ShareToken != null ? $"https://{tenant.ShareToken}.share.{_baseDomain}" : null,
        FullHistory = member is { LimitTo24Hours: false },
        Scopes = ComputeScopes(member),
        LastAccessedAt = tenant.ShareLastAccessedAt,
    };

    /// <summary>
    /// The public-shareable read scopes the Public subject currently resolves to — the union of any
    /// role-granted permissions and direct permissions, narrowed to <see cref="TenantPermissions.PublicShareScopes"/>.
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
            .Where(TenantPermissions.PublicShareScopes.Contains)
            .Distinct()
            .ToList();
    }
}
