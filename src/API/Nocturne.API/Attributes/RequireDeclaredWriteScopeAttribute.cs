using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Attributes;

/// <summary>
/// Implemented by a controller that declares a single OAuth scope covering all of its write
/// actions. <see cref="RequireDeclaredWriteScopeAttribute"/> reads the declaration and enforces it.
/// </summary>
/// <remarks>
/// Declaring the scope on the controller (rather than repeating a
/// <see cref="RequireScopeAttribute"/> on every action) lets a shared base class carry the
/// enforcement attribute while each concrete controller supplies its own data category. When the
/// member is abstract on that base class, the compiler requires every derived controller to
/// declare one.
/// </remarks>
/// <seealso cref="RequireDeclaredWriteScopeAttribute"/>
/// <seealso cref="Scope"/>
public interface IWriteScopedController
{
    /// <summary>
    /// The OAuth scope required to execute this controller's write actions, from
    /// <see cref="Scope"/>.
    /// </summary>
    string WriteScope { get; }
}

/// <summary>
/// Requires the OAuth scope the controller declares through
/// <see cref="IWriteScopedController.WriteScope"/>. Denies the request when the controller declares
/// no scope, so an action carrying this attribute can never execute unauthorized.
/// </summary>
/// <remarks>
/// <para>
/// Runs as an action filter, not an authorization filter: the required scope comes from the
/// controller instance, which <see cref="AuthorizationFilterContext"/> does not carry. Short
/// circuiting here still precedes the action body, so no record is created, updated, or deleted.
/// </para>
/// <para>
/// Method attributes are inherited by overrides, so placing this on a base-class action covers
/// every derived controller's override of it.
/// </para>
/// </remarks>
/// <seealso cref="IWriteScopedController"/>
/// <seealso cref="RequireScopeAttribute"/>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireDeclaredWriteScopeAttribute : Attribute, IActionFilter
{
    /// <summary>
    /// Evaluates the controller's declared write scope against the current request's granted scopes.
    /// </summary>
    /// <param name="context">The action filter context.</param>
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.HttpContext.IsAuthenticated())
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        if (context.Controller is not IWriteScopedController { WriteScope: { Length: > 0 } writeScope })
        {
            context.Result = new ForbidResult();
            return;
        }

        if (!Scope.Satisfies(context.HttpContext.GetGrantedScopes(), writeScope))
            context.Result = new ForbidResult();
    }

    /// <summary>No-op; the check runs before the action executes.</summary>
    /// <param name="context">The action executed context.</param>
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
