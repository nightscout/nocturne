using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Attributes;

/// <summary>
/// Restricts an endpoint to requests authenticated via <c>X-Instance-Key</c> only.
/// Regular cookie/token authenticated users will receive 403 Forbidden.
/// Used for server-to-server cross-tenant endpoints that must not be reachable
/// from a normal user session.
/// </summary>
/// <remarks>
/// Checks the <see cref="AuthContext.AuthType"/> set by <see cref="Middleware.AuthenticationMiddleware"/>
/// and only allows <see cref="AuthType.InstanceKey"/>. All other auth types (including
/// <see cref="AuthType.ApiKey"/>, <see cref="AuthType.SessionCookie"/>, etc.) are rejected.
/// </remarks>
/// <seealso cref="Middleware.AuthenticationMiddleware"/>
/// <seealso cref="AuthContext"/>
/// <seealso cref="AuthType"/>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class RequireInstanceKeyAuthAttribute : Attribute, IAuthorizationFilter
{
    /// <summary>
    /// Returns 403 Forbidden unless the request was authenticated with <see cref="AuthType.InstanceKey"/>.
    /// </summary>
    /// <param name="context">The authorization filter context.</param>
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var authContext = context.HttpContext.Items["AuthContext"] as AuthContext;
        if (authContext?.AuthType != AuthType.InstanceKey)
        {
            context.Result = new ForbidResult();
        }
    }
}
