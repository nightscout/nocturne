using Microsoft.EntityFrameworkCore;
using Nocturne.API.Extensions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Scope = Nocturne.Core.Models.Authorization.Scope;
using ScopeTranslator = Nocturne.Core.Models.Authorization.ScopeTranslator;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware that resolves the authenticated user's tenant membership and applies
/// RBAC-based permission restrictions. Effective permissions are the union of all
/// role permissions + direct permissions; <see cref="MemberScopeResolver"/> turns them into the
/// granted scope set, intersecting with the credential's own scopes unless the credential carries
/// none (<see cref="MemberScopeResolver.UnscopedCredentialTypes"/>).
/// Must run after <see cref="AuthenticationMiddleware"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline order (position 6 of 7 custom middleware):
/// <see cref="JsonExtensionMiddleware"/>,
/// <see cref="OidcCallbackRedirectMiddleware"/>, <see cref="Multitenancy.TenantResolutionMiddleware"/>,
/// <see cref="TenantSetupMiddleware"/>, <see cref="AuthenticationMiddleware"/>,
/// <b>MemberScopeMiddleware</b>, <see cref="SiteSecurityMiddleware"/>.
/// </para>
/// <para>
/// Reads the <see cref="AuthContext"/> set by <see cref="AuthenticationMiddleware"/> and
/// replaces <c>HttpContext.Items[AuthContextKeys.GrantedScopes]</c> and <c>HttpContext.Items[AuthContextKeys.PermissionTrie]</c>
/// with membership-scoped values. Uses <see cref="ScopeTranslator"/> to convert between
/// Shiro-style permissions and OAuth scopes.
/// </para>
/// </remarks>
/// <seealso cref="AuthenticationMiddleware"/>
/// <seealso cref="SiteSecurityMiddleware"/>
/// <seealso cref="PermissionTrie"/>
public class MemberScopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<MemberScopeMiddleware> _logger;

    /// <summary>
    /// Creates a new instance of <see cref="MemberScopeMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger for membership resolution diagnostics.</param>
    public MemberScopeMiddleware(RequestDelegate next, ILogger<MemberScopeMiddleware> logger)
    {
        _next = next;
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
        if (authContext is not { IsAuthenticated: true, TenantId: not null })
        {
            await _next(context);
            return;
        }

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

            await _next(context);
            return;
        }

        // Guest sessions get their scopes directly from the grant — no membership lookup
        if (authContext.AuthType == AuthType.Guest)
        {
            var guestScopes = Scope.Normalize(authContext.Scopes);
            context.SetGrantedScopes((IReadOnlySet<string>)guestScopes);
            var guestPermissions = ScopeTranslator.ToPermissions(guestScopes);
            var guestTrie = new PermissionTrie();
            guestTrie.Add(guestPermissions);
            context.SetPermissionTrie(guestTrie);
            await _next(context);
            return;
        }

        // Remaining handlers require a SubjectId for membership lookup. The development-mode
        // auto-authentication in AuthenticationMiddleware mints an AuthType.ApiKey context with no
        // subject, Permissions=["*"] and no Scopes. It used to hit the ApiKey branch above, where
        // normalizing an empty Scopes list produced no scopes and an empty trie; it now returns
        // here instead and keeps the wildcard trie AuthenticationMiddleware built from those
        // Permissions. That widening is the intended behaviour of dev auto-auth, and the path is
        // unreachable outside Development.
        if (authContext.SubjectId is null)
        {
            await _next(context);
            return;
        }

        var dbContext = context.RequestServices.GetRequiredService<NocturneDbContext>();

        var membership = await dbContext.TenantMembers
            .AsNoTracking()
            .Include(tm => tm.MemberRoles)
                .ThenInclude(mr => mr.TenantRole)
            .Where(tm => tm.SubjectId == authContext.SubjectId.Value
                         && tm.TenantId == authContext.TenantId.Value)
            .FirstOrDefaultAsync();

        if (membership == null)
        {
            // Let the existing AuthenticationMiddleware membership check handle this.
            // AuthType.ApiKey is the one credential that reaches here with no membership row:
            // AuthenticationMiddleware exempts it from that check, so an api-secret grant whose
            // subject is not a member of the tenant keeps the grant's own scopes. The grant row is
            // matched on TenantId, so those scopes are still confined to this tenant.
            //
            // The trie is a separate carrier from GrantedScopes and must be rebuilt here.
            // ApiKeyHandler sets Scopes and leaves Permissions empty, so the trie
            // AuthenticationMiddleware built is empty, and PolicyNames.HasPermissions — carried at
            // class level by every V1/V2/V3 controller — succeeds only on a non-empty trie.
            //
            // Gated on ApiKey rather than written unconditionally: every other credential type is
            // rejected by AuthenticationMiddleware's membership check before reaching this, so
            // today the gate is a no-op. It is here so that adding a type to that check's exemption
            // list cannot silently hand the new type grant-scoped access plus a matching trie.
            if (authContext.AuthType is AuthType.ApiKey)
            {
                var grantTrie = new PermissionTrie();
                grantTrie.Add(ScopeTranslator.ToPermissions(context.GetGrantedScopes()));
                context.SetPermissionTrie(grantTrie);
            }

            await _next(context);
            return;
        }

        // Resolve effective permissions: union of role permissions + direct permissions
        var rolePermissions = membership.MemberRoles
            .SelectMany(mr => mr.TenantRole.Permissions);
        var directPermissions = membership.DirectPermissions ?? [];
        var effectivePermissions = rolePermissions.Union(directPermissions).ToHashSet();

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

        authContext.LimitTo24Hours = membership.LimitTo24Hours;

        _logger.LogDebug(
            "Member {SubjectId} on tenant {TenantId} resolved with {PermCount} effective permissions (LimitTo24Hours={LimitTo24Hours})",
            authContext.SubjectId, authContext.TenantId, effectivePermissions.Count, membership.LimitTo24Hours);

        // Fire-and-forget LastUsedAt update (debounced: only if > 5 min since last update).
        // Skipped for AuthType.ApiKey, which reaches this branch only now that api-secret
        // credentials resolve through membership. These columns back the "Last active" line on the
        // member card, which reports when the person was last active and from where; an uploader
        // polling on their key is not the member logging in, and attributing it would overwrite
        // that with the uploader's IP and user-agent. The grant row has its own LastUsedAt,
        // maintained by ApiKeyHandler, which is where key activity belongs.
        if (authContext.AuthType is not AuthType.ApiKey
            && (membership.LastUsedAt == null
                || (DateTime.UtcNow - membership.LastUsedAt.Value).TotalMinutes > 5))
        {
            var membershipId = membership.Id;
            var tenantId = authContext.TenantId.Value;
            var ip = context.Connection.RemoteIpAddress?.ToString();
            var userAgent = context.Request.Headers.UserAgent.FirstOrDefault();
            var serviceScopeFactory = context.RequestServices.GetRequiredService<IServiceScopeFactory>();

            _ = Task.Run(async () =>
            {
                try
                {
                    // The fresh scope outlives the request, so its context resolves without an
                    // ambient tenant and carries no pin of its own. Pin it, and key the update on
                    // the tenant as well as the membership id.
                    using var scope = serviceScopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
                    db.TenantId = tenantId;
                    await db.TenantMembers
                        .Where(tm => tm.Id == membershipId && tm.TenantId == tenantId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(tm => tm.LastUsedAt, DateTime.UtcNow)
                            .SetProperty(tm => tm.LastUsedIp, ip)
                            .SetProperty(tm => tm.LastUsedUserAgent, userAgent));
                }
                catch (Exception ex)
                {
                    // Best-effort — don't let tracking failures affect the request. Warned rather
                    // than debugged: a write the database refuses would otherwise be invisible at
                    // production log levels. The 5-minute refresh window bounds the rate to one
                    // per member.
                    _logger.LogWarning(
                        ex, "Failed to record last-used for membership {MembershipId}", membershipId);
                }
            });
        }

        await _next(context);
    }
}
