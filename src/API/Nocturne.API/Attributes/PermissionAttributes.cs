using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Extensions;

namespace Nocturne.API.Attributes;

/// <summary>
/// Attribute to require specific Shiro-style permissions for controller actions.
/// Checks permissions against the <see cref="Nocturne.Core.Models.PermissionTrie"/> populated
/// by <see cref="Middleware.AuthenticationMiddleware"/> and refined by <see cref="Middleware.MemberScopeMiddleware"/>.
/// </summary>
/// <remarks>
/// Uses <see cref="Extensions.HttpContextExtensions.IsAuthenticated"/> and
/// <see cref="Extensions.HttpContextExtensions.HasPermission"/> to evaluate access.
/// Can be combined with <see cref="RequireScopeAttribute"/> on the same endpoint for
/// dual permission + scope enforcement.
/// </remarks>
/// <seealso cref="RequireScopeAttribute"/>
/// <seealso cref="RequireAdminAttribute"/>
/// <seealso cref="RequireReadAttribute"/>
/// <seealso cref="RequireWriteAttribute"/>
/// <seealso cref="Middleware.AuthenticationMiddleware"/>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequirePermissionAttribute : Attribute, IAuthorizationFilter
{
    private readonly string[] _permissions;
    private readonly bool _requireAll;

    /// <summary>
    /// Initialize with required permissions and combination logic.
    /// </summary>
    /// <param name="requireAll">Whether all permissions are required (<see langword="true"/>) or any one (<see langword="false"/>).</param>
    /// <param name="permissions">Required Shiro-style permission strings (e.g., <c>api:entries:read</c>).</param>
    public RequirePermissionAttribute(bool requireAll, params string[] permissions)
    {
        _permissions = permissions;
        _requireAll = requireAll;
    }

    /// <summary>
    /// Evaluates the permission requirement against the current request's <see cref="Nocturne.Core.Models.PermissionTrie"/>.
    /// </summary>
    /// <param name="context">The authorization filter context.</param>
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var httpContext = context.HttpContext;

        // Check if user is authenticated
        if (!httpContext.IsAuthenticated())
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        // Check permissions
        var hasPermission = _requireAll
            ? _permissions.All(p => httpContext.HasPermission(p))
            : _permissions.Any(p => httpContext.HasPermission(p));

        if (!hasPermission)
        {
            context.Result = new ForbidResult();
            return;
        }
    }
}

/// <summary>
/// Attribute to require authentication (but no specific permissions).
/// Returns 401 if the request was not authenticated by <see cref="Middleware.AuthenticationMiddleware"/>.
/// </summary>
/// <seealso cref="RequirePermissionAttribute"/>
/// <seealso cref="Middleware.AuthenticationMiddleware"/>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireAuthenticationAttribute : Attribute, IAuthorizationFilter
{
    /// <summary>
    /// Returns 401 Unauthorized if the request is not authenticated.
    /// </summary>
    /// <param name="context">The authorization filter context.</param>
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var httpContext = context.HttpContext;

        if (!httpContext.IsAuthenticated())
        {
            context.Result = new UnauthorizedResult();
            return;
        }
    }
}

/// <summary>
/// Attribute to require superuser permissions. Matches <c>*</c> only.
/// </summary>
/// <seealso cref="RequirePermissionAttribute"/>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireAdminAttribute : RequirePermissionAttribute
{
    public RequireAdminAttribute()
        : base(false, "*") { }
}

/// <summary>
/// Attribute to require read permissions. Matches <c>*</c>, <c>api:*</c>, <c>api:*:read</c>, or <c>readable</c>.
/// </summary>
/// <seealso cref="RequirePermissionAttribute"/>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireReadAttribute : RequirePermissionAttribute
{
    public RequireReadAttribute()
        : base(false, "*", "api:*", "api:*:read", "readable") { }
}

/// <summary>
/// Attribute to require write permissions. Matches <c>*</c>, <c>api:*</c>, <c>api:*:create</c>, <c>api:*:update</c>, or <c>api:*:delete</c>.
/// </summary>
/// <seealso cref="RequirePermissionAttribute"/>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireWriteAttribute : RequirePermissionAttribute
{
    public RequireWriteAttribute()
        : base(false, "*", "api:*", "api:*:create", "api:*:update", "api:*:delete") { }
}
