using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nocturne.Core.Models;
using Nocturne.API.Extensions;

namespace Nocturne.API.Authorization;

/// <summary>
/// Authorization requirement that succeeds when the request has any granted permissions
/// in the <see cref="PermissionTrie"/>, regardless of whether the user is authenticated.
/// </summary>
/// <remarks>
/// Registered under the <see cref="PolicyNames.HasPermissions"/> policy name.
/// The <see cref="PermissionTrie"/> is populated by <see cref="Middleware.AuthenticationMiddleware"/>
/// and refined by <see cref="Middleware.MemberScopeMiddleware"/>.
/// </remarks>
/// <seealso cref="HasPermissionsHandler"/>
/// <seealso cref="PermissionTrie"/>
/// <seealso cref="Middleware.AuthenticationMiddleware"/>
public class HasPermissionsRequirement : IAuthorizationRequirement;

/// <summary>
/// Well-known ASP.NET Core authorization policy names.
/// </summary>
public static class PolicyNames
{
    /// <summary>
    /// Policy that requires a non-empty <see cref="PermissionTrie"/> on the request.
    /// Evaluated by <see cref="HasPermissionsHandler"/>.
    /// </summary>
    public const string HasPermissions = "HasPermissions";
}

/// <summary>
/// Handles <see cref="HasPermissionsRequirement"/> by checking for a non-empty
/// <see cref="PermissionTrie"/> in <c>HttpContext.Items</c>.
/// </summary>
/// <seealso cref="HasPermissionsRequirement"/>
/// <seealso cref="PermissionTrie"/>
public class HasPermissionsHandler : AuthorizationHandler<HasPermissionsRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Creates a new instance of <see cref="HasPermissionsHandler"/>.
    /// </summary>
    /// <param name="httpContextAccessor">Accessor for the current HTTP context.</param>
    public HasPermissionsHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Succeeds the requirement if a non-empty <see cref="PermissionTrie"/> exists in <c>HttpContext.Items</c>.
    /// </summary>
    /// <remarks>
    /// A hub method invocation always succeeds. From .NET 11 SignalR evaluates the fallback policy
    /// for a hub method that carries any attribute, the compiler's own on an async method included
    /// (dotnet/aspnetcore#69683), against the trie of the request that opened the connection. The
    /// hubs authenticate in-band, after that request, so the trie of a private tenant is still
    /// empty when <c>Authorize</c> is invoked. <see cref="Hubs.HubAuthorizationFilter"/> gates every
    /// hub method on the connection's own credential.
    /// </remarks>
    /// <param name="context">The authorization handler context.</param>
    /// <param name="requirement">The <see cref="HasPermissionsRequirement"/> being evaluated.</param>
    /// <returns>A completed task.</returns>
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        HasPermissionsRequirement requirement)
    {
        if (context.Resource is HubInvocationContext)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var httpContext = _httpContextAccessor.HttpContext;

        if (httpContext?.Items[AuthContextKeys.PermissionTrie] is PermissionTrie trie && !trie.IsEmpty)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
