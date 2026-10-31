using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Scope = Nocturne.Core.Models.Authorization.Scope;
using ScopeTranslator = Nocturne.Core.Models.Authorization.ScopeTranslator;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware that resolves the authenticated user's tenant membership and applies
/// RBAC-based permission restrictions. Effective permissions are the union of all
/// role permissions + direct permissions; <see cref="MemberScopeResolver"/> turns them into the
/// granted scope set, intersecting with the credential's own scopes unless the credential carries
/// none (<see cref="MemberScopeResolver.UnscopedCredentialTypes"/>).
/// </summary>
/// <remarks>
/// <para>
/// Must run after <see cref="AuthenticationMiddleware"/>: reads the <see cref="AuthContext"/> it sets
/// and the <see cref="RequestMembership"/> it read, and replaces <c>HttpContext.Items[AuthContextKeys.GrantedScopes]</c> and <c>HttpContext.Items[AuthContextKeys.PermissionTrie]</c>
/// with membership-scoped values. Uses <see cref="ScopeTranslator"/> to convert between
/// Shiro-style permissions and OAuth scopes.
/// </para>
/// </remarks>
/// <seealso cref="AuthenticationMiddleware"/>
/// <seealso cref="PermissionTrie"/>
public class MemberScopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITenantMembershipRepository _detachedMembers;
    private readonly ILogger<MemberScopeMiddleware> _logger;

    /// <summary>
    /// Creates a new instance of <see cref="MemberScopeMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="detachedMembers">
    /// Resolved from the root with the middleware, so it reads no request's state; used for the
    /// last-used write that outlives the request.
    /// </param>
    /// <param name="logger">Logger for membership resolution diagnostics.</param>
    public MemberScopeMiddleware(
        RequestDelegate next,
        ITenantMembershipRepository detachedMembers,
        ILogger<MemberScopeMiddleware> logger)
    {
        _next = next;
        _detachedMembers = detachedMembers;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the authenticated user's tenant membership, computes effective permissions,
    /// and restricts granted scopes accordingly.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the middleware has finished processing.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var authContext = context.GetAuthContext();

        // Only process authenticated users with a resolved tenant
        if (authContext is { IsAuthenticated: true, TenantId: { } tenantId })
        {
            await ResolveAsync(context, authContext, tenantId);

            // Outside ResolveAsync because its early returns (a guest, a credential with no
            // membership) still carry the credential's own limit.
            if (authContext.LimitTo24Hours)
                context.ClampMemberHistory();
        }

        await _next(context);
    }

    private async Task ResolveAsync(HttpContext context, AuthContext authContext, Guid tenantId)
    {
        // InstanceKey: infrastructure auth, always superuser — no membership lookup needed.
        // PlatformAccess: a platform-admin tenant-access grant, pinned to this tenant and verified
        // by PlatformAccessCookieHandler — full superuser on the granted tenant, no membership.
        if (authContext.AuthType is AuthType.InstanceKey or AuthType.PlatformAccess)
        {
            var superuserScopes = new HashSet<string> { "*" };
            context.SetGrantedScopes((IReadOnlySet<string>)superuserScopes);

            var permissionTrie = new PermissionTrie();
            permissionTrie.Add(["*"]);
            context.SetPermissionTrie(permissionTrie);

            return;
        }

        // Guest sessions get their scopes directly from the grant — no membership lookup
        if (authContext.AuthType == AuthType.Guest)
        {
            var guestScopes = Scope.NormalizeGuest(authContext.Scopes);
            context.SetGrantedScopes((IReadOnlySet<string>)guestScopes);
            var guestPermissions = ScopeTranslator.ToPermissions(guestScopes);
            var guestTrie = new PermissionTrie();
            guestTrie.Add(guestPermissions);
            context.SetPermissionTrie(guestTrie);
            return;
        }

        // Remaining handlers require a SubjectId for membership lookup. The development-mode
        // auto-authentication in AuthenticationMiddleware mints an AuthType.ApiKey context with no
        // subject, Permissions=["*"] and no Scopes, and keeps the wildcard trie
        // AuthenticationMiddleware built from those Permissions. That is the intended behaviour of
        // dev auto-auth, and the path is unreachable outside Development.
        if (authContext.SubjectId is null)
        {
            return;
        }

        // AuthenticationMiddleware read the membership and has already reduced a non-member to
        // anonymous, or to the scopeless identity an invite route keeps; neither gains anything here.
        if (RequestMembership.Of(context, authContext.SubjectId.Value, tenantId)?.Membership is not { } membership)
        {
            return;
        }

        var effectivePermissions = membership.Access.EffectivePermissions;

        var resolvedScopes = MemberScopeResolver.Resolve(
            effectivePermissions, authContext.AuthType, context.GetGrantedScopes());
        context.SetGrantedScopes(resolvedScopes);

        // Rebuild the permission trie from the resolved scopes. Both must be set: GrantedScopes
        // drives RequireScope checks, while the trie drives the HasPermissions policy (the legacy
        // v1/v2/v3 endpoints). The trie AuthenticationMiddleware built holds only the subject's
        // global role permissions — empty for a member whose access comes from tenant membership —
        // so without rebuilding it here every HasPermissions-gated endpoint 403s. ScopeTranslator
        // collapses a resolved set containing "*" to a wildcard trie.
        var memberTrie = new PermissionTrie();
        memberTrie.Add(ScopeTranslator.ToPermissions(resolvedScopes));
        context.SetPermissionTrie(memberTrie);

        authContext.LimitTo24Hours = MemberScopeResolver.IsHistoryClamped(
            authContext.LimitTo24Hours, membership.Access.LimitTo24Hours, effectivePermissions);

        _logger.LogDebug(
            "Member {SubjectId} on tenant {TenantId} resolved with {PermCount} effective permissions (LimitTo24Hours={LimitTo24Hours})",
            authContext.SubjectId, authContext.TenantId, effectivePermissions.Count, membership.Access.LimitTo24Hours);

        // Fire-and-forget LastUsedAt update, debounced by LastUsedStamp.
        // Skipped for AuthType.ApiKey. These columns back the "Last active" line on the
        // member card, which reports when the person was last active and from where; an uploader
        // polling on their key is not the member logging in, and attributing it would overwrite
        // that with the uploader's IP and user-agent. The grant row has its own LastUsedAt,
        // maintained by ApiKeyHandler, which is where key activity belongs.
        if (authContext.AuthType is not AuthType.ApiKey
            && LastUsedStamp.IsDue(membership.LastUsedAt, DateTime.UtcNow))
        {
            var membershipId = membership.Id;
            var ip = context.Connection.RemoteIpAddress?.ToString();
            var userAgent = context.Request.Headers.UserAgent.FirstOrDefault();

            _ = Task.Run(async () =>
            {
                try
                {
                    // Outlives the request, so it reads nothing of the request's scope: the repository
                    // is the root one and the tenant is pinned from a captured value.
                    await _detachedMembers.RecordUseAsync(
                        tenantId, membershipId, DateTime.UtcNow, ip, userAgent, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Best-effort — don't let tracking failures affect the request. Warned rather
                    // than debugged: a write the database refuses would otherwise be invisible at
                    // production log levels. <see cref="LastUsedStamp"/> bounds the rate to one per
                    // member.
                    _logger.LogWarning(
                        ex, "Failed to record last-used for membership {MembershipId}", membershipId);
                }
            });
        }
    }
}
