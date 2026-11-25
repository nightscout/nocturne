using Nocturne.API.Services.Identity;
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
    private readonly GrantConsentService _consent;
    private readonly TimeProvider _time;
    private readonly ILogger<OAuthGrantService> _logger;

    /// <summary>
    /// Initialises a new <see cref="OAuthGrantService"/>.
    /// </summary>
    /// <param name="contexts">Opens the unit of work a consent or a revoke runs on.</param>
    /// <param name="grants">The stored grants. <see cref="IsGrantLiveAsync"/> pins the token's
    /// tenant rather than reading its scope's: it runs inside the authentication handlers, which may
    /// hold a scope with no tenant resolved.</param>
    /// <param name="clientService">Used to resolve client metadata (currently unused in this implementation).</param>
    /// <param name="guestSessionCache">Cache evicted when a grant's scopes are narrowed.</param>
    /// <param name="grantRevocation">Applies the consequences of a revoke.</param>
    /// <param name="consent">Writes the grant a consent creates or widens.</param>
    /// <param name="time">The clock a grant's expiry is judged against.</param>
    /// <param name="logger">Logger instance.</param>
    public OAuthGrantService(
        INocturneDbContextFactory contexts,
        IOAuthGrantRepository grants,
        IOAuthClientService clientService,
        GuestSessionCacheService guestSessionCache,
        GrantRevocationService grantRevocation,
        GrantConsentService consent,
        TimeProvider time,
        ILogger<OAuthGrantService> logger)
    {
        _time = time;
        _contexts = contexts;
        _grants = grants;
        _clientService = clientService;
        _guestSessionCache = guestSessionCache;
        _grantRevocation = grantRevocation;
        _consent = consent;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OAuthGrantInfo?> CreateOrUpdateGrantAsync(
        Guid clientEntityId,
        Guid subjectId,
        IEnumerable<string> scopes,
        bool limitTo24Hours = false,
        CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(_contexts, ct);
        StagedGrantConsent? consent = null;
        await unit.CommitAsync(
            async token => consent = await _consent.StageAsync(
                unit, clientEntityId, subjectId, scopes, limitTo24Hours, ct: token),
            ct);

        if (consent is null)
            return null;

        _consent.LogCommitted(consent);
        return consent.Grant;
    }

    /// <inheritdoc />
    public async Task<OAuthGrantInfo?> GetActiveGrantAsync(
        Guid clientEntityId,
        Guid subjectId,
        CancellationToken ct = default)
    {
        var grant = await _grants.FindActiveAsync(clientEntityId, subjectId, ct);
        return grant == null ? null : OAuthGrantMapper.ToInfo(grant);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OAuthGrantInfo>> GetGrantsForSubjectAsync(
        Guid subjectId,
        CancellationToken ct = default)
    {
        var grants = await _grants.GetActiveForSubjectAsync(subjectId, ct);
        return grants.Select(OAuthGrantMapper.ToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<OAuthGrantInfo?> GetGrantForSubjectAsync(
        Guid grantId,
        Guid ownerSubjectId,
        CancellationToken ct = default)
    {
        var grant = await FindUnrevokedOfSubjectAsync(grantId, ownerSubjectId, ct);
        return grant is null ? null : OAuthGrantMapper.ToInfo(grant);
    }

    private async Task<OAuthGrantRecord?> FindUnrevokedOfSubjectAsync(
        Guid grantId, Guid ownerSubjectId, CancellationToken ct)
    {
        var grant = await _grants.FindOfSubjectAsync(grantId, ownerSubjectId, ct);
        return grant is { IsRevoked: false } ? grant : null;
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

        var outcome = await _grantRevocation.StageAsync(unit, grant, _time.GetUtcNow().UtcDateTime, ct);
        await unit.CommitAsync(token => unit.Context.SaveChangesAsync(token), ct);
        _grantRevocation.EvictSessions([grant]);

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} subject_id={SubjectId} revoked_tokens={TokenCount} revoked_devices={DeviceCount}",
            "grant_revoked", grantId, grant.SubjectId, outcome.RevokedRefreshTokenCount, outcome.RemovedDeviceCount);
    }

    /// <inheritdoc />
    public async Task<bool> IsGrantLiveAsync(
        Guid grantId,
        Guid tenantId,
        CancellationToken ct = default)
    {
        var state = await _grants.GetStateAsync(tenantId, grantId, ct);

        // No row means the grant was deleted, belongs to another tenant, or never existed.
        return state is not null && state.IsLiveAt(_time.GetUtcNow().UtcDateTime);
    }

    /// <inheritdoc />
    public Task UpdateLastUsedAsync(
        Guid grantId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default) =>
        _grants.RecordUseAsync(grantId, _time.GetUtcNow().UtcDateTime, new GrantPresentation(ipAddress, userAgent), ct);

    /// <inheritdoc />
    public async Task<OAuthGrantInfo?> UpdateGrantAsync(
        Guid grantId,
        Guid ownerSubjectId,
        GrantScopeBounds bounds,
        string? label = null,
        IEnumerable<string>? scopes = null,
        CancellationToken ct = default)
    {
        var grant = await FindUnrevokedOfSubjectAsync(grantId, ownerSubjectId, ct);

        if (grant == null)
            return null;

        if (Scope.ValidateDelegation(scopes, bounds.OwnCredential) is { } exceedsOwner)
            throw new GrantCeilingViolationException(exceedsOwner);

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

        if (validatedScopes is not null && grant.GrantType == OAuthGrantTypes.Guest)
        {
            var violation = bounds.GivenToOthers is null
                ? new GrantCeilingViolation(CallerAccessFromTeam.Code, CallerAccessFromTeam.Detail)
                : Scope.ValidateDelegation(validatedScopes, bounds.GivenToOthers);
            if (violation is not null)
                throw new GrantCeilingViolationException(violation);
        }

        var updated = await _grants.UpdateAsync(grant.Id, new OAuthGrantEdit(label, validatedScopes), ct)
            ?? throw new InvalidOperationException($"Grant {grant.Id} vanished while it was edited.");

        // The cached guest session carries the grant's scopes, so narrowing a guest link's scopes
        // would otherwise leave the wider set live for the rest of the 30-second TTL.
        _guestSessionCache.Evict(grant.TenantId, grant.Id);

        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} subject_id={SubjectId}",
            "grant_modified", grantId, ownerSubjectId);

        return OAuthGrantMapper.ToInfo(updated);
    }
}
