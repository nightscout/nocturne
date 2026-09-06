using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Hubs;

/// <summary>
/// What kind of credential a hub connection presented. Scopes say which data categories the
/// connection may see; this says whether it belongs to the tenant or is a narrow share of it, which
/// is what decides access to the groups that carry more than one category.
/// </summary>
/// <remarks>
/// "Belongs to the tenant" is what the handshake proved, which for most types is a tenant-membership
/// row — <see cref="Middleware.AuthenticationMiddleware"/> rejects a subject that has none.
/// <see cref="AuthType.ApiKey"/> is the type it exempts from that check (see
/// <see cref="Middleware.MemberScopeMiddleware"/>: an api-secret grant row is matched on TenantId and
/// keeps the grant's own scopes), so an api-secret credential whose subject is not a member of the
/// tenant still classifies as <see cref="Subject"/> and reaches the tenant-wide and subject groups on
/// its grant alone.
/// </remarks>
/// <seealso cref="HubAuthorization.CanJoinTenantRelay"/>
public enum HubCredentialKind
{
    /// <summary>
    /// A share of the tenant's data with no account behind it: a guest link. May join the data
    /// category groups it holds a read scope for, and nothing else — the tenant-wide groups carry
    /// other members' in-app notifications and alert state, which no guest scope covers.
    /// </summary>
    /// <remarks>
    /// The default, so a credential whose kind was not classified cannot reach a tenant-wide group.
    /// </remarks>
    Restricted = 0,

    /// <summary>
    /// A credential belonging to a subject: a member session, an OAuth app grant, a follower or
    /// direct grant, or platform access. Joins the tenant-wide live data group and its own subject
    /// group.
    /// </summary>
    Subject,

    /// <summary>
    /// The instance key — infrastructure auth, currently the socket.io bridge. Additionally joins
    /// <see cref="Services.Realtime.RealtimeGroups.Relay"/>, because it relays every subject's
    /// payloads onward itself rather than consuming them.
    /// </summary>
    Infrastructure,
}

/// <summary>
/// The credential a hub connection has proven: the tenant it is pinned to, the OAuth scopes it
/// carries, what kind of credential it is, and the subject behind it when there is one.
/// </summary>
/// <param name="TenantId">The tenant the credential authorizes, always the connection's own tenant.</param>
/// <param name="Scopes">The granted OAuth scopes, normalized.</param>
/// <param name="Kind">
/// The credential's kind. Required rather than defaulted so every construction site states it.
/// </param>
/// <param name="SubjectId">
/// The subject the credential belongs to, or null when it belongs to none (the instance key, and a
/// guest session — <c>GuestSessionHandler</c> leaves <c>SubjectId</c> null and records the data owner
/// it acts for on <c>ActingAsSubjectId</c>). Read only through <see cref="OwnSubjectId"/>, which
/// refuses a non-<see cref="HubCredentialKind.Subject"/> credential a subject group whatever it
/// carries here.
/// </param>
public sealed record HubAuthorization(
    Guid TenantId,
    IReadOnlySet<string> Scopes,
    HubCredentialKind Kind,
    Guid? SubjectId)
{
    /// <summary>Whether the credential satisfies <paramref name="scope"/>.</summary>
    public bool Satisfies(string scope) => Scope.Satisfies(Scopes, scope);

    /// <summary>
    /// Whether the credential may join a group carrying the whole tenant's live payloads rather than
    /// a single data category.
    /// </summary>
    /// <remarks>
    /// False for a share-style grant. Those groups are gated on <c>glucose.read</c> alone but carry
    /// tracker state, device action intents and arbitrary <c>dataUpdate</c> payloads, so a read-only
    /// share holding glucose would otherwise receive categories it was never granted.
    /// </remarks>
    public bool CanJoinTenantRelay => Kind is not HubCredentialKind.Restricted;

    /// <summary>
    /// Whether the credential is the infrastructure bridge, which relays per-subject payloads to its
    /// own clients and therefore needs the tenant-wide copy of them.
    /// </summary>
    public bool IsInfrastructure => Kind is HubCredentialKind.Infrastructure;

    /// <summary>
    /// The subject group this credential owns, or null when it owns none. A share-style credential
    /// owns none even if a subject id reaches <see cref="SubjectId"/> by some route.
    /// </summary>
    public Guid? OwnSubjectId => Kind is HubCredentialKind.Subject ? SubjectId : null;

    /// <summary>
    /// Classifies the credential an HTTP upgrade handshake authenticated.
    /// </summary>
    /// <remarks>
    /// Lists the types that belong to the tenant, so an authentication type added later lands on
    /// <see cref="HubCredentialKind.Restricted"/> and has to be classified deliberately before it
    /// reaches a tenant-wide group. A public share never reaches here: it resolves with
    /// <c>IsAuthenticated: false</c> and so cannot authorize a hub connection at all.
    /// </remarks>
    /// <param name="authType">The authentication type resolved for the handshake.</param>
    public static HubCredentialKind Classify(AuthType authType) => authType switch
    {
        AuthType.InstanceKey => HubCredentialKind.Infrastructure,

        AuthType.OidcToken
            or AuthType.LegacyJwt
            or AuthType.LegacyAccessToken
            or AuthType.ApiKey
            or AuthType.SessionCookie
            or AuthType.OAuthAccessToken
            or AuthType.DirectGrant
            or AuthType.PlatformAccess => HubCredentialKind.Subject,

        _ => HubCredentialKind.Restricted,
    };
}

/// <summary>
/// Marks a hub method as an in-band authentication entry point: it is reachable on a connection that
/// has not yet proven a credential, and records the result with
/// <see cref="HubAuthorizationState.Grant"/> when one is presented. Every hub method without this
/// attribute is denied on an unauthorized connection by <see cref="HubAuthorizationFilter"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HubAuthenticationMethodAttribute : Attribute;

/// <summary>
/// The OAuth scope a hub method requires, enforced by <see cref="HubAuthorizationFilter"/> against
/// the connection's <see cref="HubAuthorization.Scopes"/>. A method without it still requires an
/// authorized connection; declare a scope on any method that reads or changes tenant data.
/// </summary>
/// <param name="scope">The required scope, from <see cref="Scope"/>.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HubScopeAttribute(string scope) : Attribute
{
    /// <summary>The required scope.</summary>
    public string Scope { get; } = scope;
}

/// <summary>
/// Marks a hub method as joining a group that carries the whole tenant's live payloads rather than a
/// single data category. <see cref="HubAuthorizationFilter"/> refuses the invocation unless the
/// connection's credential satisfies <see cref="HubAuthorization.CanJoinTenantRelay"/>, so the check
/// is declared once per method instead of written out at each join.
/// </summary>
/// <remarks>
/// A method carrying <see cref="HubAuthenticationMethodAttribute"/> as well runs before the filter can
/// see a credential — the credential arrives in the invocation itself — so it checks
/// <see cref="HubAuthorization.CanJoinTenantRelay"/> against what it just authenticated. The attribute
/// is declared on those methods too, which is what lets
/// <c>HubAuthorizationFilterTests.Every_hub_method_that_joins_a_tenant_wide_group_declares_it</c> hold
/// every method that joins one to it: a method added later that joins a tenant-wide group without
/// declaring it fails that test rather than shipping ungated.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HubTenantGroupAttribute : Attribute;

/// <summary>
/// Reads and writes the <see cref="HubAuthorization"/> carried by a SignalR connection.
/// </summary>
public static class HubAuthorizationState
{
    /// <summary>Key the authorization is stored under in <see cref="HubCallerContext.Items"/>.</summary>
    private const string ItemKey = "HubAuthorization";

    /// <summary>
    /// Records the result of a successful in-band authentication on the connection. Only an
    /// authentication method that has verified a credential may call this.
    /// </summary>
    /// <param name="context">The connection to record the credential on.</param>
    /// <param name="authorization">The verified credential.</param>
    public static HubAuthorization Grant(HubCallerContext context, HubAuthorization authorization)
    {
        context.Items[ItemKey] = authorization;
        return authorization;
    }

    /// <summary>
    /// The connection's proven credential, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Falls back to the HTTP upgrade handshake. A connection that presented a session cookie,
    /// api-secret or bearer token on the upgrade request was already authenticated by
    /// <see cref="Middleware.AuthenticationMiddleware"/> and scoped by
    /// <see cref="Middleware.MemberScopeMiddleware"/>, so it needs no in-band handshake. Clients that
    /// present their credential only after the connection is up authenticate in-band instead.
    /// </remarks>
    public static HubAuthorization? Resolve(HubCallerContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var existing)
            && existing is HubAuthorization granted)
        {
            return granted;
        }

        var httpContext = context.GetHttpContext();

        // GetAuthContext() reads Items["AuthContext"] as Core.Models.Authorization.AuthContext —
        // the type AuthenticationMiddleware actually stores there.
        if (httpContext?.GetAuthContext() is not { IsAuthenticated: true } authContext)
        {
            return null;
        }

        if (httpContext.Items[TenantAwareHub.TenantContextKey] is not TenantContext tenantContext)
        {
            return null;
        }

        // Scopes come from the handshake, which is where membership and token scopes were resolved;
        // they cannot change for the life of the connection.
        return Grant(context, new HubAuthorization(
            tenantContext.TenantId,
            httpContext.GetGrantedScopes(),
            HubAuthorization.Classify(authContext.AuthType),
            authContext.SubjectId));
    }
}

/// <summary>
/// SignalR hub filter that denies every hub method invocation on a connection that has not proven a
/// credential, and enforces the scope declared by <see cref="HubScopeAttribute"/> and the credential
/// kind required by <see cref="HubTenantGroupAttribute"/>.
/// </summary>
/// <remarks>
/// The hubs are <c>[AllowAnonymous]</c> because in-band authentication requires the connection to be
/// accepted before the credential arrives, and <see cref="TenantAwareHub.OnConnectedAsync"/> accepts
/// any connection whose tenant resolves and is active. Authorization therefore has to happen at the
/// method boundary. It lives in a filter rather than in each method so a method added later is
/// denied unless it explicitly opts out with <see cref="HubAuthenticationMethodAttribute"/>.
/// </remarks>
public sealed class HubAuthorizationFilter : IHubFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var method = invocationContext.HubMethod;

        if (method.GetCustomAttribute<HubAuthenticationMethodAttribute>() is null)
        {
            var authorization = HubAuthorizationState.Resolve(invocationContext.Context)
                ?? throw new HubException($"{method.Name} requires an authorized connection.");

            var requiredScope = method.GetCustomAttribute<HubScopeAttribute>()?.Scope;
            if (requiredScope is not null && !authorization.Satisfies(requiredScope))
            {
                throw new HubException($"{method.Name} requires the {requiredScope} scope.");
            }

            if (method.GetCustomAttribute<HubTenantGroupAttribute>() is not null
                && !authorization.CanJoinTenantRelay)
            {
                throw new HubException(
                    $"{method.Name} requires a credential belonging to the tenant.");
            }
        }

        return await next(invocationContext);
    }
}
