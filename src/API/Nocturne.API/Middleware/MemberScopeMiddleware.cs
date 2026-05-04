using Microsoft.EntityFrameworkCore;
using Nocturne.API.Extensions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using OAuthScopes = Nocturne.Core.Models.Authorization.OAuthScopes;
using ScopeTranslator = Nocturne.Core.Models.Authorization.ScopeTranslator;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware that resolves the authenticated user's tenant membership and applies
/// RBAC-based permission restrictions. Effective permissions are the union of all
/// role permissions + direct permissions. For non-superusers, effective permissions
/// are intersected with the auth token's granted scopes via <see cref="OAuthScopes"/>.
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
/// replaces <c>HttpContext.Items["GrantedScopes"]</c> and <c>HttpContext.Items["PermissionTrie"]</c>
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

        // InstanceKey: infrastructure auth, always superuser — no membership lookup needed
        if (authContext.AuthType is AuthType.InstanceKey)
        {
            var superuserScopes = new HashSet<string> { "*" };
            context.Items["GrantedScopes"] = (IReadOnlySet<string>)superuserScopes;

            var permissionTrie = new PermissionTrie();
            permissionTrie.Add(["*"]);
            context.Items["PermissionTrie"] = permissionTrie;

            await _next(context);
            return;
        }

        // ApiKey: use the grant's actual scopes, skip membership lookup
        if (authContext.AuthType is AuthType.ApiKey)
        {
            var grantedScopes = OAuthScopes.Normalize(authContext.Scopes);
            context.Items["GrantedScopes"] = grantedScopes;

            var permissions = ScopeTranslator.ToPermissions(grantedScopes);
            var permissionTrie = new PermissionTrie();
            permissionTrie.Add(permissions);
            context.Items["PermissionTrie"] = permissionTrie;

            await _next(context);
            return;
        }

        // Guest sessions get their scopes directly from the grant — no membership lookup
        if (authContext.AuthType == AuthType.Guest)
        {
            var guestScopes = OAuthScopes.Normalize(authContext.Scopes);
            context.Items["GrantedScopes"] = (IReadOnlySet<string>)guestScopes;
            var guestPermissions = ScopeTranslator.ToPermissions(guestScopes);
            var guestTrie = new PermissionTrie();
            guestTrie.Add(guestPermissions);
            context.Items["PermissionTrie"] = guestTrie;
            await _next(context);
            return;
        }

        // Remaining handlers require a SubjectId for membership lookup
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
                         && tm.TenantId == authContext.TenantId.Value
                         && tm.RevokedAt == null)
            .FirstOrDefaultAsync();

        if (membership == null)
        {
            // Let the existing AuthenticationMiddleware membership check handle this
            await _next(context);
            return;
        }

        // Resolve effective permissions: union of role permissions + direct permissions
        var rolePermissions = membership.MemberRoles
            .SelectMany(mr => mr.TenantRole.Permissions);
        var directPermissions = membership.DirectPermissions ?? [];
        var effectivePermissions = rolePermissions.Union(directPermissions).Distinct().ToHashSet();

        if (effectivePermissions.Contains("*"))
        {
            // Superuser — grant all scopes directly
            context.Items["GrantedScopes"] = (IReadOnlySet<string>)effectivePermissions;
        }
        else
        {
            // Intersect with auth token scopes
            var normalizedMemberScopes = OAuthScopes.Normalize(effectivePermissions.ToList());
            var currentScopes = context.GetGrantedScopes();
            var restrictedScopes = normalizedMemberScopes
                .Where(memberScope => OAuthScopes.SatisfiesScope(currentScopes, memberScope))
                .ToHashSet();

            context.Items["GrantedScopes"] = (IReadOnlySet<string>)restrictedScopes;

            // Rebuild permission trie from restricted scopes
            var restrictedPermissions = ScopeTranslator.ToPermissions(restrictedScopes);
            var permissionTrie = new PermissionTrie();
            permissionTrie.Add(restrictedPermissions);
            context.Items["PermissionTrie"] = permissionTrie;
        }

        authContext.LimitTo24Hours = membership.LimitTo24Hours;

        _logger.LogDebug(
            "Member {SubjectId} on tenant {TenantId} resolved with {PermCount} effective permissions (LimitTo24Hours={LimitTo24Hours})",
            authContext.SubjectId, authContext.TenantId, effectivePermissions.Count, membership.LimitTo24Hours);

        // Fire-and-forget LastUsedAt update (debounced: only if > 5 min since last update)
        if (membership.LastUsedAt == null ||
            (DateTime.UtcNow - membership.LastUsedAt.Value).TotalMinutes > 5)
        {
            var membershipId = membership.Id;
            var ip = context.Connection.RemoteIpAddress?.ToString();
            var userAgent = context.Request.Headers.UserAgent.FirstOrDefault();
            var serviceScopeFactory = context.RequestServices.GetRequiredService<IServiceScopeFactory>();

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = serviceScopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
                    await db.TenantMembers
                        .Where(tm => tm.Id == membershipId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(tm => tm.LastUsedAt, DateTime.UtcNow)
                            .SetProperty(tm => tm.LastUsedIp, ip)
                            .SetProperty(tm => tm.LastUsedUserAgent, userAgent));
                }
                catch
                {
                    // Best-effort — don't let tracking failures affect the request
                }
            });
        }

        await _next(context);
    }
}
