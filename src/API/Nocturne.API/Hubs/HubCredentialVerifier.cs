using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Hubs;

/// <summary>
/// What a hub credential was proven from: the server-side records that keep it valid, and what it
/// carries of its own before a membership narrows it.
/// </summary>
/// <param name="AuthType">How the credential authenticated.</param>
/// <param name="SubjectId">The subject it belongs to; null for the instance key and a guest.</param>
/// <param name="GrantId">The grant (or guest link) behind it, when there is one.</param>
/// <param name="CredentialScopes">Its own scopes, before a membership intersects them.</param>
/// <param name="LimitTo24Hours">Its own history limit, before a membership's is combined.</param>
/// <param name="ExpiresAt">
/// When the presented token lapses. Read only for a credential with no server-side record to prove
/// it by (a grant-less token) and for platform access, whose token is the time-boxed grant itself.
/// A grant-backed token or a session login is renewed while its record stands, so its token's
/// lifetime says nothing about whether the access behind it does.
/// </param>
/// <param name="SessionId">The refresh-token family a session login belongs to.</param>
public sealed record HubCredentialSource(
    AuthType AuthType,
    Guid? SubjectId,
    Guid? GrantId,
    IReadOnlySet<string> CredentialScopes,
    bool LimitTo24Hours,
    DateTimeOffset? ExpiresAt,
    string? SessionId = null);

/// <summary>
/// The tenants an <see cref="OverviewHub"/> connection was admitted to, and the credential that
/// admitted it.
/// </summary>
public sealed record OverviewGrant(
    Guid SubjectId,
    AuthType AuthType,
    IReadOnlySet<string> TokenScopes,
    IReadOnlyCollection<Guid> TenantIds,
    string? SessionId = null);

/// <summary>
/// Proves hub credentials again from server-side state: the tenant still exists and is active, the
/// grant or guest link is live, the subject is active, a session login still has a session, and the
/// membership still exists. Scopes and the history clamp are recomputed as <see cref="Middleware.MemberScopeMiddleware"/> does.
/// </summary>
/// <remarks>
/// One instance serves one sweep, and memoizes every lookup for it, so connections that share a
/// subject, a grant or a tenant cost one read between them.
/// </remarks>
public sealed class HubCredentialVerifier(IServiceProvider services, TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<object, Lazy<Task<object?>>> _memo = new();
    private readonly DateTime _now = timeProvider.GetUtcNow().UtcDateTime;

    private async Task<T?> Memo<T>(object key, Func<Task<T?>> load) where T : class =>
        (T?)await _memo.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () => await load())).Value;

    private Task<OAuthGrantState?> GrantAsync(Guid tenantId, Guid grantId) =>
        Memo(("grant", tenantId, grantId), () =>
            services.GetRequiredService<IOAuthGrantRepository>().GetStateAsync(tenantId, grantId, CancellationToken.None));

    private Task<SubjectSignInState?> SubjectAsync(Guid subjectId) =>
        Memo(("subject", subjectId), () =>
            services.GetRequiredService<ISubjectRepository>().GetSignInStateAsync(subjectId, CancellationToken.None));

    /// <summary>
    /// Whether the session a login belongs to still has a live refresh token, as
    /// <see cref="IFirstPartyTokenRepository.HasLiveSessionAsync"/> judges it.
    /// </summary>
    private async Task<bool> HasSessionAsync(Guid subjectId, string? sessionId) =>
        await Memo<object>(("session", subjectId, sessionId), async () =>
            await services.GetRequiredService<IFirstPartyTokenRepository>()
                .HasLiveSessionAsync(subjectId, sessionId, _now, CancellationToken.None)) is true;

    private Task<TenantMemberAccess?> MembershipAsync(Guid subjectId, Guid tenantId) =>
        Memo(("member", subjectId, tenantId), () =>
            services.GetRequiredService<ITenantMemberService>().GetMemberAccessAsync(subjectId, tenantId));

    private Task<GuestSessionInfo?> GuestSessionAsync(TenantContext tenant, Guid grantId) =>
        Memo(("guest", tenant.TenantId, grantId), async () =>
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(tenant);
            return await scope.ServiceProvider.GetRequiredService<IGuestLinkService>().ValidateSessionAsync(grantId);
        });

    private async Task<bool> IsTokenRevokedAsync(Guid jti) =>
        await Memo<object>(("jti", jti), async () =>
            await services.GetRequiredService<IOAuthTokenRevocationCache>().IsRevokedAsync(jti.ToString())) is true;

    private async Task<bool> IsTenantActiveAsync(Guid tenantId) =>
        await Memo<object>(("tenant", tenantId), async () =>
            await services.GetRequiredService<ITenantRepository>().IsActiveAsync(tenantId, CancellationToken.None)) is true;

    private bool IsLive(OAuthGrantState? grant) =>
        grant is { RevokedAt: null } && (grant.ExpiresAt is null || grant.ExpiresAt > _now);

    /// <summary>
    /// The credential <paramref name="granted"/> holds now, or null when it no longer authorizes
    /// anything. Compare the two with <see cref="HubAuthorization.IsCoveredBy"/>.
    /// </summary>
    /// <param name="granted">The credential a connection was admitted on.</param>
    /// <param name="tenant">The connection's tenant.</param>
    public async Task<HubAuthorization?> ProveAsync(HubAuthorization granted, TenantContext tenant)
    {
        if (granted.Source is not { } source || tenant.TenantId != granted.TenantId)
            return null;

        // The connection's tenant was read at its handshake. A slug resolves afresh on every request, so
        // a tenant deleted and recreated under its slug is a different id, and the old one reads as gone.
        if (!await IsTenantActiveAsync(tenant.TenantId))
            return null;

        switch (source.AuthType)
        {
            case AuthType.InstanceKey:
                return granted;

            case AuthType.Guest:
            {
                if (source.GrantId is not { } guestGrant
                    || await GuestSessionAsync(tenant, guestGrant) is not { } session
                    || session.TenantId != tenant.TenantId)
                {
                    return null;
                }

                return granted with
                {
                    Scopes = Scope.NormalizeGuest(session.Scopes),
                    HistoryClamped = session.LimitTo24Hours,
                };
            }

            case AuthType.PlatformAccess:
                return source.SubjectId is { } admin
                       && await SubjectAsync(admin) is { IsActive: true, IsPlatformAdmin: true }
                       && (source.ExpiresAt is null || source.ExpiresAt > timeProvider.GetUtcNow())
                       && !(source.GrantId is { } jti && await IsTokenRevokedAsync(jti))
                    ? granted
                    : null;
        }

        // Development auto-authentication mints a subjectless, grantless api-secret context.
        if (source.SubjectId is not { } subjectId)
            return source is { AuthType: AuthType.ApiKey, GrantId: null } ? granted : null;

        if (await SubjectAsync(subjectId) is not { IsActive: true })
            return null;

        var credentialScopes = source.CredentialScopes;
        var credentialLimit = source.LimitTo24Hours;

        if (source.AuthType is AuthType.ApiKey or AuthType.DirectGrant)
        {
            if (source.GrantId is not { } grantId || await GrantAsync(tenant.TenantId, grantId) is not { } grant
                || !IsLive(grant))
            {
                return null;
            }

            credentialScopes = source.AuthType is AuthType.ApiKey ? Scope.Normalize(grant.Scopes) : grant.Scopes.ToHashSet();
            credentialLimit = grant.LimitTo24Hours;
        }
        else if (source.GrantId is { } tokenGrant)
        {
            if (!IsLive(await GrantAsync(tenant.TenantId, tokenGrant)))
                return null;
        }
        else if (source.AuthType is AuthType.SessionCookie)
        {
            if (!await HasSessionAsync(subjectId, source.SessionId))
                return null;
        }
        else if (source.ExpiresAt is not { } expiresAt || expiresAt <= timeProvider.GetUtcNow())
        {
            // Nothing server-side stands behind a grant-less token, so it lasts only as long as
            // the token itself.
            return null;
        }

        if (await MembershipAsync(subjectId, tenant.TenantId) is not { } membership)
        {
            // An api-secret grant needs no membership; MemberScopeMiddleware keeps its own scopes.
            return source.AuthType is AuthType.ApiKey
                ? granted with { Scopes = credentialScopes, HistoryClamped = credentialLimit }
                : null;
        }

        return granted with
        {
            Scopes = MemberScopeResolver.Resolve(membership.EffectivePermissions, source.AuthType, credentialScopes),
            HistoryClamped = MemberScopeResolver.IsHistoryClamped(
                credentialLimit, membership.LimitTo24Hours, membership.EffectivePermissions),
        };
    }

    /// <summary>
    /// Whether an <see cref="OverviewHub"/> connection still reaches every tenant it was admitted
    /// to: its subject is active, a session login still has a session, and the overview service
    /// still resolves each tenant for it.
    /// </summary>
    public async Task<bool> StillHoldsAsync(OverviewGrant grant)
    {
        if (await SubjectAsync(grant.SubjectId) is not { IsActive: true })
            return false;

        if (grant.AuthType is AuthType.SessionCookie && !await HasSessionAsync(grant.SubjectId, grant.SessionId))
            return false;

        var tenants = await services.GetRequiredService<ITenantOverviewService>()
            .GetGlucoseReadTenantsAsync(grant.SubjectId, grant.TokenScopes, grant.AuthType);
        var reachable = tenants.Select(t => t.Tenant.Id).ToHashSet();
        return grant.TenantIds.All(reachable.Contains);
    }
}
