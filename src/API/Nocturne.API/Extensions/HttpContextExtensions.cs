using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.API.Middleware;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Scope = Nocturne.Core.Models.Authorization.Scope;

namespace Nocturne.API.Extensions;

/// <summary>
/// The keys the auth pipeline parks its state under on <see cref="HttpContext.Items"/>.
/// </summary>
/// <remarks>
/// The bag is untyped and the keys were previously spelled out at every read site, so a typo read
/// as "absent" — which every consumer treats as unauthenticated or unscoped. Nothing outside
/// <see cref="HttpContextExtensions"/> should name them.
/// </remarks>
internal static class AuthContextKeys
{
    internal const string AuthContext = "AuthContext";
    internal const string LegacyAuthContext = "AuthenticationContext";
    internal const string GrantedScopes = "GrantedScopes";
    internal const string PermissionTrie = "PermissionTrie";
    internal const string TenantContext = "TenantContext";
    internal const string ShareAccess = "ShareAccess";
}

/// <summary>
/// Typed access to the auth state the middleware pipeline leaves on the request.
/// </summary>
/// <remarks>
/// Two different questions live here and they are NOT interchangeable:
/// <see cref="HasPermission"/> asks the legacy Nightscout <see cref="PermissionTrie"/>, which is
/// rebuilt from the resolved scopes and speaks the <c>api:entries:read</c> vocabulary;
/// <see cref="HasScope"/> asks the granted scope set directly in the <c>glucose.read</c>
/// vocabulary. A caller wanting a tenant permission atom wants <see cref="HasScope"/>.
/// </remarks>
public static class HttpContextExtensions
{
    /// <summary>
    /// Get the new-style authentication context from the request
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Authentication context or null if not set</returns>
    public static AuthContext? GetAuthContext(this HttpContext context)
    {
        return context.Items[AuthContextKeys.AuthContext] as AuthContext;
    }

    /// <summary>
    /// Get the legacy authentication context from the request (for backward compatibility)
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Legacy authentication context</returns>
    public static AuthenticationContext GetLegacyAuthContext(this HttpContext context)
    {
        return context.Items[AuthContextKeys.LegacyAuthContext] as AuthenticationContext
            ?? new AuthenticationContext();
    }

    /// <summary>
    /// Check if the current request has a specific permission
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="permission">Permission to check</param>
    /// <returns>True if permission is granted</returns>
    public static bool HasPermission(this HttpContext context, string permission)
    {
        var permissionTrie = context.Items[AuthContextKeys.PermissionTrie] as PermissionTrie;
        if (permissionTrie == null)
        {
            return false;
        }

        return permissionTrie.Check(permission);
    }

    /// <summary>
    /// Check if the current request is authenticated
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>True if authenticated</returns>
    public static bool IsAuthenticated(this HttpContext context)
    {
        return context.GetAuthContext()?.IsAuthenticated ?? false;
    }

    /// <summary>
    /// Get the subject ID for the current request
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Subject ID or null if not authenticated</returns>
    public static Guid? GetSubjectId(this HttpContext context)
    {
        return context.GetAuthContext()?.SubjectId;
    }

    /// <summary>
    /// Get the subject ID as a string for the current request (legacy compatibility)
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Subject ID as string or null if not authenticated</returns>
    public static string? GetSubjectIdString(this HttpContext context)
    {
        return context.GetAuthContext()?.SubjectId?.ToString();
    }

    /// <summary>
    /// Check if the current request has admin permissions
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>True if has admin permissions</returns>
    public static bool IsAdmin(this HttpContext context)
    {
        return context.HasPermission("admin") || context.HasPermission("*");
    }

    /// <summary>
    /// Check if the current request has read permissions
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>True if has read permissions</returns>
    public static bool CanRead(this HttpContext context)
    {
        return context.HasPermission("*")
            || context.HasPermission("api:*")
            || context.HasPermission("api:*:read")
            || context.HasPermission("readable");
    }

    /// <summary>
    /// Check if the current request has write permissions
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>True if has write permissions</returns>
    public static bool CanWrite(this HttpContext context)
    {
        return context.HasPermission("*")
            || context.HasPermission("api:*")
            || context.HasPermission("api:*:create")
            || context.HasPermission("api:*:update")
            || context.HasPermission("api:*:delete");
    }

    /// <summary>
    /// Get the resolved OAuth scopes for the current request.
    /// These are populated by the auth middleware from either OAuth token claims
    /// or translated from legacy Shiro-style permissions.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Set of granted scope strings</returns>
    public static IReadOnlySet<string> GetGrantedScopes(this HttpContext context)
    {
        if (context.Items[AuthContextKeys.GrantedScopes] is IReadOnlySet<string> scopes)
        {
            return scopes;
        }

        return new HashSet<string>();
    }

    /// <summary>
    /// Check if the current request has a specific OAuth scope.
    /// Handles readwrite implying read, and * implying everything.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="scope">The scope to check</param>
    /// <returns>True if the scope is satisfied</returns>
    public static bool HasScope(this HttpContext context, string scope)
    {
        var grantedScopes = context.GetGrantedScopes();
        return Scope.Satisfies(grantedScopes, scope);
    }

    /// <summary>
    /// The tenant the request resolved to, or <see langword="null"/> when it resolved to none.
    /// </summary>
    public static TenantContext? GetTenantContext(this HttpContext context)
    {
        return context.Items[AuthContextKeys.TenantContext] as TenantContext;
    }

    /// <summary>Records the resolved tenant for the rest of the pipeline.</summary>
    public static void SetTenantContext(this HttpContext context, TenantContext tenantContext)
    {
        context.Items[AuthContextKeys.TenantContext] = tenantContext;
    }

    /// <summary>
    /// The legacy permission trie the request resolved to, or <see langword="null"/> when the
    /// credential produced none.
    /// </summary>
    /// <seealso cref="HasPermission"/>
    public static PermissionTrie? GetPermissionTrie(this HttpContext context)
    {
        return context.Items[AuthContextKeys.PermissionTrie] as PermissionTrie;
    }

    /// <summary>Records the resolved permission trie.</summary>
    public static void SetPermissionTrie(this HttpContext context, PermissionTrie trie)
    {
        context.Items[AuthContextKeys.PermissionTrie] = trie;
    }

    /// <summary>Records the resolved granted scopes.</summary>
    public static void SetGrantedScopes(this HttpContext context, IReadOnlySet<string> scopes)
    {
        context.Items[AuthContextKeys.GrantedScopes] = scopes;
    }

    /// <summary>
    /// Records the legacy authentication context v1-v3 read through
    /// <see cref="GetLegacyAuthContext"/>. Written from the same place as the modern one, so the
    /// pair cannot fall out of step.
    /// </summary>
    public static void SetLegacyAuthContext(
        this HttpContext context, AuthenticationContext legacyContext)
    {
        context.Items[AuthContextKeys.LegacyAuthContext] = legacyContext;
    }

    /// <summary>Records the resolved auth context.</summary>
    public static void SetAuthContext(this HttpContext context, AuthContext authContext)
    {
        context.Items[AuthContextKeys.AuthContext] = authContext;
    }

    /// <summary>
    /// Whether the request arrived on a public share link (<c>{token}.share.{domain}</c>), which is
    /// anonymous and category-narrowed rather than a signed-in session.
    /// </summary>
    public static bool IsShareAccess(this HttpContext context)
    {
        return context.Items[AuthContextKeys.ShareAccess] is true;
    }

    /// <summary>Marks the request as arriving on a public share link.</summary>
    public static void SetShareAccess(this HttpContext context)
    {
        context.Items[AuthContextKeys.ShareAccess] = true;
    }
}
