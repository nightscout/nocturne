using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Runs the gate an action declares: the synchronous authorization filters, then the synchronous
/// action filters, from the controller's attributes and the method's, in the order MVC runs them.
/// A filter that also implements the asynchronous interface is skipped, as MVC runs only that path
/// for it, and it would reach for request services a unit test does not build.
/// </summary>
internal static class ActionGate
{
    /// <summary>
    /// The short-circuit result of the first filter that refuses the call, or null when the request
    /// would reach the handler. A null <paramref name="authContext"/> is an unauthenticated caller.
    /// </summary>
    public static IActionResult? Evaluate(
        Type controller,
        string methodName,
        IReadOnlySet<string> grantedScopes,
        AuthContext? authContext = null) =>
        Evaluate(controller, Method(controller, methodName), grantedScopes, authContext);

    public static IActionResult? Evaluate(
        Type controller,
        MethodInfo method,
        IReadOnlySet<string> grantedScopes,
        AuthContext? authContext = null)
    {
        var httpContext = new DefaultHttpContext();
        if (authContext is not null)
            httpContext.Items["AuthContext"] = authContext;
        httpContext.Items["GrantedScopes"] = grantedScopes;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filters = Filters(controller, method);

        foreach (var filter in filters.OfType<IAuthorizationFilter>().Where(f => f is not IAsyncAuthorizationFilter))
        {
            var authorizationContext = new AuthorizationFilterContext(actionContext, []);
            filter.OnAuthorization(authorizationContext);
            if (authorizationContext.Result is not null)
                return authorizationContext.Result;
        }

        var executingContext = new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            RuntimeHelpers.GetUninitializedObject(controller));

        foreach (var filter in filters.OfType<IActionFilter>().Where(f => f is not IAsyncActionFilter))
        {
            filter.OnActionExecuting(executingContext);
            if (executingContext.Result is not null)
                return executingContext.Result;
        }

        return null;
    }

    private static MethodInfo Method(Type controller, string methodName) =>
        controller.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            $"{controller.Name} has no public instance method named {methodName}; the gate moved with it");

    /// <summary>
    /// MVC sorts by <see cref="IOrderedFilter.Order"/>, then by scope with the controller's before
    /// the action's. The sort is stable, so listing the controller's attributes first gives that.
    /// </summary>
    private static IEnumerable<object> Filters(Type controller, MethodInfo method) =>
        controller.GetCustomAttributes(inherit: true)
            .Concat(method.GetCustomAttributes(inherit: true))
            .OrderBy(a => a is IOrderedFilter ordered ? ordered.Order : 0);
}
