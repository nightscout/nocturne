using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages OAuth 2.0 authorisation grants stored in the database, including creation,
/// scope merging, revocation, and cascade-revocation of associated refresh tokens.
/// </summary>
/// <seealso cref="IOAuthGrantService"/>
/// <seealso cref="IOAuthClientService"/>
public class OAuthGrantService : IOAuthGrantService
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly IOAuthGrantRepository _grants;
    private readonly IOAuthClientService _clientService;
    private readonly GuestSessionCacheService _guestSessionCache;
    private readonly GrantRevocationService _grantRevocation;
    private readonly ILogger<OAuthGrantService> _logger;

    /// <summary>
    /// Initialises a new <see cref="OAuthGrantService"/>.
    /// </summary>
    /// <param name="contexts">Opens the unit of work a revoke runs on.</param>
    /// <param name="grants">The stored grants. <see cref="IsGrantRevokedAsync"/> pins the token's
    /// tenant rather than reading its scope's: it runs inside the authentication handlers, which may
    /// hold a scope with no tenant resolved.</param>
    /// <param name="clientService">Used to resolve client metadata (currently unused in this implementation).</param>
    /// <param name="guestSessionCache">Cache evicted when a grant's scopes are narrowed.</param>
    /// <param name="grantRevocation">Applies the consequences of a revoke.</param>
    /// <param name="logger">Logger instance.</param>
    public OAuthGrantService(
        INocturneDbContextFactory contexts,
        IOAuthGrantRepository grants,
        IOAuthClientService clientService,
        GuestSessionCacheService guestSessionCache,
        GrantRevocationService grantRevocation,
        ILogger<OAuthGrantService> logger)
    {
        _contexts = contexts;
        _grants = grants;
        _clientService = clientService;
        _guestSessionCache = guestSessionCache;
        _grantRevocation = grantRevocation;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OAuthGrantInfo> CreateOrUpdateGrantAsync(
        Guid clientEntityId,
        Guid subjectId,
        IEnumerable<string> scopes,
        string grantType = OAuthGrantTypes.App,
        string? label = null,
        bool limitTo24Hours = false,
        bool approvedByDeviceCode = false,
        CancellationToken ct = default)
    {
        var existingGrant = await _grants.FindActiveAsync(clientEntityId, subjectId, ct);

        if (existingGrant != null)
        {
            // Merge scopes: union of existing and new
            var mergedScopes = existingGrant.Scopes
                .Union(scopes)
                .OrderBy(s => s)
                .ToList();

            var updated = await _grants.UpdateAsync(
                existingGrant.Id,
                new OAuthGrantEdit(label, mergedScopes, limitTo24Hours, approvedByDeviceCode),
                ct) ?? throw new InvalidOperationException($"Grant {existingGrant.Id} vanished while its consent was merged.");

            _logger.LogInformation(
                "OAuthAudit: {Event} grant_id={GrantId} subject_id={SubjectId} client_entity_id={ClientEntityId} scopes={Scopes}",
                "grant_updated", existingGrant.Id, subjectId, clientEntityId, string.Join(" ", mergedScopes));

            return ToInfo(updated);
        }

        var scopeList = scopes.Distinct().OrderBy(s => s).ToList();

        var grantId = await _grants.CreateAsync(
            new NewOAuthGrant(
                clientEntityId,
                subjectId,
                grantType,
                scopeList,
                label,
                TokenHash: null,
                LegacySecretHash: null,
                ExpiresAt: null,
                limitTo24Hours,
                approvedByDeviceCode),
            ct);
        var grant = await _grants.GetAsync(grantId, ct)
            ?? throw new InvalidOperationException($"Grant {grantId} is not readable after its save.");

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} subject_id={SubjectId} client_entity_id={ClientEntityId} scopes={Scopes}",
            "grant_created", grant.Id, subjectId, clientEntityId, string.Join(" ", scopeList));

        return ToInfo(grant);
    }

    /// <inheritdoc />
    public async Task<OAuthGrantInfo?> GetActiveGrantAsync(
        Guid clientEntityId,
        Guid subjectId,
        CancellationToken ct = default)
    {
        var grant = await _grants.FindActiveAsync(clientEntityId, subjectId, ct);
        return grant == null ? null : ToInfo(grant);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OAuthGrantInfo>> GetGrantsForSubjectAsync(
        Guid subjectId,
        CancellationToken ct = default)
    {
        var grants = await _grants.GetActiveForSubjectAsync(subjectId, ct);
        return grants.Select(ToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<OAuthGrantInfo?> GetGrantForSubjectAsync(
        Guid grantId,
        Guid ownerSubjectId,
        CancellationToken ct = default)
    {
        var grant = await _grants.FindOfSubjectAsync(grantId, ownerSubjectId, ct);
        return grant is null || grant.IsRevoked ? null : ToInfo(grant);
    }

    /// <inheritdoc />
    public async Task RevokeGrantAsync(Guid grantId, CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(_contexts, ct);
        var grant = await new OAuthGrantRepository(unit).GetAsync(grantId, ct);

        if (grant == null)
        {
            _logger.LogWarning("Attempted to revoke non-existent OAuth grant {GrantId}", grantId);
            return;
        }

        var outcome = await _grantRevocation.StageAsync(unit, grant, ct);
        await unit.CommitAsync(token => unit.Context.SaveChangesAsync(token), ct);
        _grantRevocation.EvictSessions([grant]);

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} subject_id={SubjectId} revoked_tokens={TokenCount} revoked_devices={DeviceCount}",
            "grant_revoked", grantId, grant.SubjectId, outcome.RevokedRefreshTokenCount, outcome.RemovedDeviceCount);
    }

    /// <inheritdoc />
    public async Task<bool> IsGrantRevokedAsync(
        Guid grantId,
        Guid tenantId,
        CancellationToken ct = default)
    {
        var state = await _grants.GetStateAsync(tenantId, grantId, ct);

        // No row means the grant was deleted, belongs to another tenant, or never existed.
        return state is null || state.RevokedAt != null;
    }

    /// <inheritdoc />
    public Task UpdateLastUsedAsync(
        Guid grantId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default) =>
        _grants.RecordUseAsync(grantId, DateTime.UtcNow, new GrantPresentation(ipAddress, userAgent), ct);

    /// <summary>The grant as the management UI and token minting see it.</summary>
    internal static OAuthGrantInfo ToInfo(OAuthGrantRecord grant)
    {
        return new OAuthGrantInfo
        {
            Id = grant.Id,
            TenantId = grant.TenantId,
            ClientEntityId = grant.ClientEntityId,
            ClientId = grant.Client?.ClientId ?? string.Empty,
            ClientDisplayName = grant.Client?.DisplayName,
            ClientUri = grant.Client?.ClientUri,
            LogoUri = grant.Client?.LogoUri,
            ClientIdentity = grant.ClientIdentity,
            SubjectId = grant.SubjectId,
            GrantType = grant.GrantType,
            Scopes = [.. grant.Scopes],
            Label = grant.Label,
            CreatedAt = grant.CreatedAt,
            LastUsedAt = grant.LastUsedAt,
            LastUsedIp = grant.LastUsedIp,
            LastUsedUserAgent = grant.LastUsedUserAgent,
            IsRevoked = grant.IsRevoked,
            LimitTo24Hours = grant.LimitTo24Hours,
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
        var grant = await _grants.FindOfSubjectAsync(grantId, ownerSubjectId, ct);

        if (grant == null)
            return null;

        // Validated before anything is written, so a rejected update leaves the stored grant
        // untouched. This method filters on the grant id and the owning subject with no GrantType
        // filter, and a guest grant records the DATA OWNER's subject id, so the owner reaches their
        // own guest link here; the cap is what stops a PATCH turning a read-only share into full
        // access.
        var validatedScopes = scopes is null
            ? null
            : Scope.ValidateGrantScopes(scopes, grant.GrantType);

        // A grant with no scopes authorizes nothing, and the tokens it mints carry no scope claim
        // to mark them as delegated.
        if (validatedScopes is { Count: 0 })
        {
            throw new ArgumentException("A grant needs at least one scope.");
        }

        var updated = await _grants.UpdateAsync(grant.Id, new OAuthGrantEdit(label, validatedScopes), ct)
            ?? throw new InvalidOperationException($"Grant {grant.Id} vanished while it was edited.");

        // The cached guest session carries the grant's scopes, so narrowing a guest link's scopes
        // would otherwise leave the wider set live for the rest of the 30-second TTL.
        _guestSessionCache.Evict(grant.TenantId, grant.Id);

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} subject_id={SubjectId}",
            "grant_modified", grantId, ownerSubjectId);

        return ToInfo(updated);
    }
}
