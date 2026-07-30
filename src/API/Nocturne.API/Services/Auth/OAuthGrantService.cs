using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages OAuth 2.0 authorisation grants stored in the database, including creation,
/// scope merging, revocation, and cascade-revocation of associated refresh tokens.
/// </summary>
/// <seealso cref="IOAuthGrantService"/>
/// <seealso cref="IOAuthClientService"/>
public class OAuthGrantService : IOAuthGrantService
{
    private readonly NocturneDbContext _dbContext;
    private readonly IDbContextFactory<NocturneDbContext> _dbContextFactory;
    private readonly IOAuthClientService _clientService;
    private readonly GuestSessionCacheService _guestSessionCache;
    private readonly ILogger<OAuthGrantService> _logger;

    /// <summary>
    /// Initialises a new <see cref="OAuthGrantService"/>.
    /// </summary>
    /// <param name="dbContext">Database context for grant persistence.</param>
    /// <param name="dbContextFactory">Factory used by <see cref="IsGrantRevokedAsync"/>, which runs
    /// during authentication and so cannot rely on the scoped context being tenant-pinned yet.</param>
    /// <param name="clientService">Used to resolve client metadata (currently unused in this implementation).</param>
    /// <param name="guestSessionCache">Cache evicted when a grant is revoked, so a revoked guest link stops resolving.</param>
    /// <param name="logger">Logger instance.</param>
    public OAuthGrantService(
        NocturneDbContext dbContext,
        IDbContextFactory<NocturneDbContext> dbContextFactory,
        IOAuthClientService clientService,
        GuestSessionCacheService guestSessionCache,
        ILogger<OAuthGrantService> logger)
    {
        _dbContext = dbContext;
        _dbContextFactory = dbContextFactory;
        _clientService = clientService;
        _guestSessionCache = guestSessionCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OAuthGrantInfo> CreateOrUpdateGrantAsync(
        Guid clientEntityId,
        Guid subjectId,
        IEnumerable<string> scopes,
        string grantType = OAuthScopes.GrantTypeApp,
        string? label = null,
        CancellationToken ct = default)
    {
        var existingGrant = await _dbContext.OAuthGrants
            .Include(g => g.Client)
            .Where(g => g.ClientEntityId == clientEntityId
                     && g.SubjectId == subjectId
                     && g.RevokedAt == null)
            .FirstOrDefaultAsync(ct);

        if (existingGrant != null)
        {
            // Merge scopes: union of existing and new
            var mergedScopes = existingGrant.Scopes
                .Union(scopes)
                .OrderBy(s => s)
                .ToList();

            existingGrant.Scopes = mergedScopes;

            if (label != null)
            {
                existingGrant.Label = label;
            }

            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation(
                "OAuthAudit: {Event} grant_id={GrantId} subject_id={SubjectId} client_entity_id={ClientEntityId} scopes={Scopes}",
                "grant_updated", existingGrant.Id, subjectId, clientEntityId, string.Join(" ", mergedScopes));

            return MapToInfo(existingGrant);
        }

        var scopeList = scopes.Distinct().OrderBy(s => s).ToList();

        var entity = new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            ClientEntityId = clientEntityId,
            SubjectId = subjectId,
            GrantType = grantType,
            Scopes = scopeList,
            Label = label,
            CreatedAt = DateTime.UtcNow,
        };

        _dbContext.OAuthGrants.Add(entity);
        await _dbContext.SaveChangesAsync(ct);

        // Load the Client navigation property for the return DTO
        await _dbContext.Entry(entity)
            .Reference(e => e.Client)
            .LoadAsync(ct);

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} subject_id={SubjectId} client_entity_id={ClientEntityId} scopes={Scopes}",
            "grant_created", entity.Id, subjectId, clientEntityId, string.Join(" ", scopeList));

        return MapToInfo(entity);
    }

    /// <inheritdoc />
    public async Task<OAuthGrantInfo?> GetActiveGrantAsync(
        Guid clientEntityId,
        Guid subjectId,
        CancellationToken ct = default)
    {
        var entity = await _dbContext.OAuthGrants
            .AsNoTracking()
            .Include(g => g.Client)
            .Where(g => g.ClientEntityId == clientEntityId
                     && g.SubjectId == subjectId
                     && g.RevokedAt == null)
            .FirstOrDefaultAsync(ct);

        if (entity == null)
        {
            return null;
        }

        return MapToInfo(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OAuthGrantInfo>> GetGrantsForSubjectAsync(
        Guid subjectId,
        CancellationToken ct = default)
    {
        var entities = await _dbContext.OAuthGrants
            .AsNoTracking()
            .Include(g => g.Client)
            .Where(g => g.SubjectId == subjectId && g.RevokedAt == null)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(MapToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task RevokeGrantAsync(Guid grantId, CancellationToken ct = default)
    {
        var grant = await _dbContext.OAuthGrants
            .Where(g => g.Id == grantId)
            .FirstOrDefaultAsync(ct);

        if (grant == null)
        {
            _logger.LogWarning("Attempted to revoke non-existent OAuth grant {GrantId}", grantId);
            return;
        }

        var now = DateTime.UtcNow;

        // Revoke the grant
        grant.RevokedAt = now;

        // Cascade revoke all associated OAuth refresh tokens
        var refreshTokens = await _dbContext.OAuthRefreshTokens
            .Where(t => t.GrantId == grantId && t.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in refreshTokens)
        {
            token.RevokedAt = now;
        }

        await _dbContext.SaveChangesAsync(ct);

        // Guest sessions are cached for 30 seconds, so revoking the grant is not enough on its
        // own. Evicting here rather than in GuestLinkService covers every revoke path: a guest
        // grant's SubjectId is the data owner, and DeleteGrant filters only on SubjectId, so the
        // owner can revoke their own guest link through the OAuth grants API without ever
        // entering GuestLinkService.
        _guestSessionCache.Evict(grant.TenantId, grant.Id);

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} subject_id={SubjectId} revoked_tokens={TokenCount}",
            "grant_revoked", grantId, grant.SubjectId, refreshTokens.Count);
    }

    /// <inheritdoc />
    public async Task<bool> IsGrantRevokedAsync(
        Guid grantId,
        Guid tenantId,
        CancellationToken ct = default)
    {
        // A dedicated context pinned to the token's tenant: this runs inside the authentication
        // handlers, which may hold a child scope whose context carries no tenant (and therefore no
        // RLS tenant GUC), and an unpinned read would return nothing for every grant.
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        db.TenantId = tenantId;

        var state = await db.OAuthGrants
            .AsNoTracking()
            .Where(g => g.Id == grantId && g.TenantId == tenantId)
            .Select(g => new { g.RevokedAt })
            .FirstOrDefaultAsync(ct);

        // No row means the grant was deleted, belongs to another tenant, or never existed.
        return state is null || state.RevokedAt != null;
    }

    /// <inheritdoc />
    public async Task UpdateLastUsedAsync(
        Guid grantId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        await _dbContext.OAuthGrants
            .Where(g => g.Id == grantId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.LastUsedAt, DateTime.UtcNow)
                .SetProperty(g => g.LastUsedIp, ipAddress)
                .SetProperty(g => g.LastUsedUserAgent, userAgent),
                ct);
    }

    /// <summary>
    /// Maps an <see cref="OAuthGrantEntity"/> to an <see cref="OAuthGrantInfo"/> DTO.
    /// </summary>
    /// <param name="entity">The database entity to map. The <c>Client</c> navigation property must be loaded.</param>
    /// <returns>A populated <see cref="OAuthGrantInfo"/> view model.</returns>
    private static OAuthGrantInfo MapToInfo(OAuthGrantEntity entity)
    {
        return new OAuthGrantInfo
        {
            Id = entity.Id,
            TenantId = entity.TenantId,
            ClientEntityId = entity.ClientEntityId,
            ClientId = entity.Client?.ClientId ?? string.Empty,
            ClientDisplayName = entity.Client?.DisplayName,
            ClientUri = entity.Client?.ClientUri,
            LogoUri = entity.Client?.LogoUri,
            IsKnownClient = entity.Client?.IsKnown ?? false,
            SubjectId = entity.SubjectId,
            GrantType = entity.GrantType,
            Scopes = entity.Scopes,
            Label = entity.Label,
            CreatedAt = entity.CreatedAt,
            LastUsedAt = entity.LastUsedAt,
            LastUsedIp = entity.LastUsedIp,
            LastUsedUserAgent = entity.LastUsedUserAgent,
            IsRevoked = entity.IsRevoked,
        };
    }

    /// <inheritdoc />
    public async Task<OAuthGrantInfo?> UpdateGrantAsync(
        Guid grantId,
        Guid ownerSubjectId,
        string? label = null,
        IEnumerable<string>? scopes = null,
        CancellationToken ct = default)
    {
        var grant = await _dbContext.OAuthGrants
            .Include(g => g.Client)
            .Where(g => g.Id == grantId)
            .FirstOrDefaultAsync(ct);

        // Grant not found or not owned by the specified subject
        if (grant == null || grant.SubjectId != ownerSubjectId)
            return null;

        // Validated before anything is assigned, so a rejected update leaves the tracked entity
        // untouched. This method filters on the grant id and the owning subject with no GrantType
        // filter, and a guest grant records the DATA OWNER's subject id, so the owner reaches their
        // own guest link here; the cap is what stops a PATCH turning a read-only share into full
        // access.
        var validatedScopes = scopes is null
            ? null
            : OAuthScopes.ValidateGrantScopes(scopes, grant.GrantType);

        if (label != null)
        {
            grant.Label = label;
        }

        if (validatedScopes != null)
        {
            grant.Scopes = validatedScopes;
        }

        await _dbContext.SaveChangesAsync(ct);

        // The cached guest session carries the grant's scopes, so narrowing a guest link's scopes
        // would otherwise leave the wider set live for the rest of the 30-second TTL. Mirrors
        // RevokeGrantAsync, and for the same reason: this path never enters GuestLinkService.
        _guestSessionCache.Evict(grant.TenantId, grant.Id);

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} subject_id={SubjectId}",
            "grant_modified", grantId, ownerSubjectId);

        return MapToInfo(grant);
    }
}
