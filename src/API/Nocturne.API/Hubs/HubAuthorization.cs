using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Hubs;

/// <summary>
/// What kind of credential a hub connection presented. Scopes say which data categories the
/// connection may see; this says whether it belongs to the tenant or is a narrow share of it, which
/// is what decides access to the groups that carry more than one category.
/// </summary>
/// <remarks>
/// "Belongs to the tenant" is what the handshake proved: a tenant-membership row, which
/// <see cref="Middleware.AuthenticationMiddleware"/> requires of every credential except the
/// instance key and platform access.
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
/// <param name="HistoryClamped">
/// Whether the credential may read only the last 24 hours: its own limit combined with its
/// membership's by <see cref="MemberScopeResolver.IsHistoryClamped"/>. Required rather than defaulted,
/// so a construction site cannot leave a limited credential reading full history by omission.
/// </param>
public sealed record HubAuthorization(
    Guid TenantId,
    IReadOnlySet<string> Scopes,
    HubCredentialKind Kind,
    Guid? SubjectId,
    bool HistoryClamped)
{
    /// <summary>
    /// The OAuth client an access token was issued to, or null for any other credential. A Home
    /// Assistant instance is addressed by it (see <see cref="HomeAssistantHub.Subscribe"/>).
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// What the credential was proven from, so <see cref="HubCredentialVerifier"/> can prove it
    /// again from server-side state. Null only where no credential source exists (a test double),
    /// and such a credential is withdrawn at the first re-check.
    /// </summary>
    public HubCredentialSource? Source { get; init; }

    /// <summary>Whether the credential satisfies <paramref name="scope"/>.</summary>
    public bool Satisfies(string scope) => Scope.Satisfies(Scopes, scope);

    /// <summary>
    /// Whether <paramref name="current"/>, the credential re-derived from the same source, still
    /// grants everything this one does: the same principal, every scope, and no new history clamp.
    /// </summary>
    /// <remarks>
    /// A widened credential passes too, and keeps the narrower access it connected with until it
    /// reconnects.
    /// </remarks>
    public bool IsCoveredBy(HubAuthorization? current) =>
        current is not null
        && current.TenantId == TenantId
        && current.Kind == Kind
        && current.SubjectId == SubjectId
        && current.ClientId == ClientId
        && (HistoryClamped || !current.HistoryClamped)
        && Scopes.All(current.Satisfies);

    /// <summary>
    /// Whether the credential may receive the live broadcasts of <paramref name="category"/>, one of
    /// <see cref="RealtimeCategories.All"/>.
    /// </summary>
    /// <remarks>
    /// A history-clamped credential receives none: a broadcast carries whatever record was just
    /// written, including a backdated one older than the 24 hours its reads are limited to, and it
    /// catches up on its own clamped reads instead.
    /// </remarks>
    public bool MayReceiveCategory(string category) =>
        !HistoryClamped
        && RealtimeCategories.GoverningScopes.TryGetValue(category, out var scope)
        && Satisfies(scope);

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
/// The OAuth scopes a hub method accepts, enforced by <see cref="HubAuthorizationFilter"/> against
/// the connection's <see cref="HubAuthorization.Scopes"/>: any one of them admits the call, as
/// with <c>RequireScope</c> over HTTP. A method without it still requires an authorized
/// connection; declare a scope on any method that reads or changes tenant data.
/// </summary>
/// <param name="scopes">The accepted scopes, from <see cref="Scope"/>.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HubScopeAttribute(params string[] scopes) : Attribute
{
    /// <summary>The accepted scopes; the call needs one of them.</summary>
    public IReadOnlyList<string> Scopes { get; } = scopes;
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

    /// <summary>Key an <see cref="OverviewGrant"/> is stored under.</summary>
    private const string OverviewItemKey = "OverviewGrant";

    /// <summary>
    /// Records the result of a successful in-band authentication on the connection. Only an
    /// authentication method that has verified a credential may call this.
    /// </summary>
    /// <remarks>
    /// A clamped credential clamps the handshake request's services, which is the scope every hub
    /// method reads tenant data through. An in-band credential never passed
    /// <see cref="Middleware.MemberScopeMiddleware"/>, so without this its reads would run unclamped.
    /// The clamp is applied before the credential is recorded, so a clamp that throws leaves the
    /// connection unauthorized rather than authorized and unclamped. It also narrows the connection's
    /// <see cref="HubConnectionReader"/>, which <see cref="TenantHubFilter"/> sets on the invocation
    /// scope of every later call; the call that authenticates in-band already has its invocation
    /// scope, so it reads tenant data through the handshake scope. The credential's principal becomes
    /// the connection's audit actor (<see cref="HubConnectionActor"/>).
    /// </remarks>
    /// <param name="context">The connection to record the credential on.</param>
    /// <param name="authorization">The verified credential.</param>
    public static HubAuthorization Grant(HubCallerContext context, HubAuthorization authorization)
    {
        if (authorization.HistoryClamped)
        {
            context.GetHttpContext()?.ClampMemberHistory();
            HubConnectionReader.ClampToDay(context);
        }
        HubConnectionActor.Record(context, authorization);
        context.Items[ItemKey] = authorization;
        return authorization;
    }

    /// <summary>Records the tenants an <see cref="OverviewHub"/> connection was admitted to.</summary>
    public static void GrantOverview(HubCallerContext context, OverviewGrant grant) =>
        context.Items[OverviewItemKey] = grant;

    /// <summary>The <see cref="OverviewHub"/> admission the connection holds, if any.</summary>
    public static OverviewGrant? Overview(HubCallerContext context) =>
        context.Items.TryGetValue(OverviewItemKey, out var value) ? value as OverviewGrant : null;

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
        if (context.Items.TryGetValue(ItemKey, out var existing) && existing is HubAuthorization granted)
            return granted;

        return context.GetHttpContext() is { } httpContext && FromRequest(httpContext) is { } authorization
            ? Grant(context, authorization)
            : null;
    }

    /// <summary>
    /// The credential an HTTP request authenticated, or null when it proved none.
    /// </summary>
    /// <remarks>
    /// The one reading of a request's credential for realtime admission. The hub applies it to its
    /// upgrade handshake and <see cref="Controllers.V4.Identity.RealtimeAdmissionController"/> to
    /// the request the socket.io bridge admits a browser on, so both keep the same credentials out
    /// of the tenant-wide groups. An anonymous public share resolves with
    /// <c>IsAuthenticated: false</c> and so proves none.
    /// </remarks>
    /// <param name="httpContext">A request that has passed authentication and tenant resolution.</param>
    public static HubAuthorization? FromRequest(HttpContext httpContext)
    {
        // GetAuthContext() reads Items["AuthContext"] as Core.Models.Authorization.AuthContext —
        // the type AuthenticationMiddleware actually stores there.
        if (httpContext.GetAuthContext() is not { IsAuthenticated: true } authContext)
        {
            return null;
        }

        if (httpContext.GetTenantContext() is not { } tenantContext)
        {
            return null;
        }

        return new HubAuthorization(
            tenantContext.TenantId,
            httpContext.GetGrantedScopes(),
            HubAuthorization.Classify(authContext.AuthType),
            authContext.SubjectId,
            authContext.LimitTo24Hours)
        {
            ClientId = authContext.ClientId,
            Source = new HubCredentialSource(
                authContext.AuthType,
                authContext.SubjectId,
                authContext.TokenId,
                Middleware.AuthenticationMiddleware.CredentialScopes(authContext),
                authContext.LimitTo24Hours,
                authContext.ExpiresAt,
                authContext.SessionId),
        };
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
/// any connection whose tenant resolves, is active and is the one it names in
/// <see cref="TenantAwareHub.ExpectedTenantHeader"/>, if it names one. Authorization therefore has to happen at the
/// method boundary. It lives in a filter rather than in each method so a method added later is
/// denied unless it explicitly opts out with <see cref="HubAuthenticationMethodAttribute"/>.
/// <para>
/// A connection that holds a credential once an invocation returns is handed to
/// <see cref="HubConnectionRevalidator"/>, which closes it when the credential no longer proves what
/// it did.
/// </para>
/// </remarks>
public sealed class HubAuthorizationFilter(HubConnectionRevalidator revalidator) : IHubFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await AuthorizeAndInvokeAsync(invocationContext, next);
        }
        finally
        {
            var context = invocationContext.Context;
            if (!context.ConnectionAborted.IsCancellationRequested
                && (HubAuthorizationState.Resolve(context) is not null
                    || HubAuthorizationState.Overview(context) is not null))
            {
                revalidator.Track(context);
            }
        }
    }

    /// <inheritdoc />
    public Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        revalidator.Forget(context.Context);
        return next(context, exception);
    }

    private static async ValueTask<object?> AuthorizeAndInvokeAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var method = invocationContext.HubMethod;

        if (method.GetCustomAttribute<HubAuthenticationMethodAttribute>() is null)
        {
            var authorization = HubAuthorizationState.Resolve(invocationContext.Context)
                ?? throw new HubException($"{method.Name} requires an authorized connection.");

            var acceptedScopes = method.GetCustomAttribute<HubScopeAttribute>()?.Scopes;
            if (acceptedScopes is not null && !acceptedScopes.Any(authorization.Satisfies))
            {
                throw new HubException(
                    $"{method.Name} requires the {string.Join(" or ", acceptedScopes)} scope.");
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
