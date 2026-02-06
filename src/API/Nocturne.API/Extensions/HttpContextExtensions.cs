using Nocturne.API.Middleware;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using OAuthScopes = Nocturne.Core.Models.Authorization.OAuthScopes;

namespace Nocturne.API.Extensions;

/// <summary>
/// Extension methods for HttpContext to handle authentication and permissions
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    /// Get the new-style authentication context from the request
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Authentication context or null if not set</returns>
    public static AuthContext? GetAuthContext(this HttpContext context)
    {
        return context.Items["AuthContext"] as AuthContext;
    }

    /// <summary>
    /// Get the legacy authentication context from the request (for backward compatibility)
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Legacy authentication context</returns>
    public static AuthenticationContext GetLegacyAuthContext(this HttpContext context)
    {
        return context.Items["AuthenticationContext"] as AuthenticationContext
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
        var authContext = context.GetAuthContext();
        if (authContext == null || !authContext.IsAuthenticated)
        {
            return false;
        }

        var permissionTrie = context.Items["PermissionTrie"] as PermissionTrie;
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
        if (context.Items["GrantedScopes"] is IReadOnlySet<string> scopes)
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
        return OAuthScopes.SatisfiesScope(grantedScopes, scope);
    }
}
