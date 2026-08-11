using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Shared reflection over the API's MVC controllers, used by the authorization guard tests to
/// enumerate actions, their routes and the gates in front of them.
/// </summary>
internal static class ControllerActionReflection
{
    /// <summary>The API assembly under audit.</summary>
    public static Assembly ApiAssembly => typeof(AuthorizationConfiguration).Assembly;

    /// <summary>Every controller MVC would discover in the API assembly.</summary>
    public static IEnumerable<Type> GetControllers() =>
        ApiAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t)
                        && t is { IsClass: true, IsAbstract: false, IsPublic: true }
                        && t.GetCustomAttribute<NonControllerAttribute>() is null);

    /// <summary>Every action method on a controller.</summary>
    public static IEnumerable<MethodInfo> GetActionMethods(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName
                        && m.GetCustomAttributes().OfType<IActionHttpMethodProvider>().Any());

    /// <summary>
    /// The absolute routes an action answers on, with a leading slash and no trailing one.
    /// </summary>
    /// <remarks>
    /// One action can carry several method attributes and therefore several routes, so this
    /// returns them all. Attribute routing only; the API declares no conventional routes.
    /// </remarks>
    public static IEnumerable<string> GetRoutes(Type controller, MethodInfo action)
    {
        var prefix = controller.GetCustomAttributes<RouteAttribute>(inherit: true)
            .Select(r => r.Template)
            .FirstOrDefault() ?? string.Empty;

        prefix = prefix.Replace(
            "[controller]",
            controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? controller.Name[..^"Controller".Length]
                : controller.Name,
            StringComparison.Ordinal);

        var templates = action.GetCustomAttributes()
            .OfType<IActionHttpMethodProvider>()
            .OfType<IRouteTemplateProvider>()
            .Select(t => t.Template)
            .ToList();

        if (templates.Count == 0)
        {
            templates.Add(null);
        }

        return templates.Select(t => Combine(prefix, t)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether the action is explicitly anonymous, itself or through its controller.</summary>
    public static bool HasAnonymous(MethodInfo action, Type controller) =>
        action.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any()
        || controller.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any();

    /// <summary>
    /// Whether the action declares an authorization gate — <c>[Authorize]</c> and friends, or one
    /// of the Nocturne <c>[Require*]</c> filters — itself or through its controller.
    /// </summary>
    public static bool HasAuthorizationGate(MethodInfo action, Type controller) =>
        HasGate(action.GetCustomAttributes(inherit: true))
        || HasGate(controller.GetCustomAttributes(inherit: true));

    /// <summary>
    /// Whether the action refuses the demo tenant's shared visitor account, itself or through
    /// its controller.
    /// </summary>
    public static bool DeniesTheDemoSubject(MethodInfo action, Type controller) =>
        action.GetCustomAttribute<DenyDemoSubjectAttribute>() is not null
        || controller.GetCustomAttribute<DenyDemoSubjectAttribute>() is not null;

    private static bool HasGate(IEnumerable<object> attributes) =>
        attributes.Any(a => a is IAuthorizeData
                            or RequirePermissionAttribute // covers [RequireAdmin]/[RequireRead]/[RequireWrite]
                            or RequireScopeAttribute
                            or RequireInstanceKeyAuthAttribute
                            or RequireAuthenticationAttribute);

    private static string Combine(string prefix, string? template)
    {
        if (!string.IsNullOrEmpty(template) && (template.StartsWith('/') || template.StartsWith("~/")))
        {
            return Normalize(template.TrimStart('~'));
        }

        var combined = string.IsNullOrEmpty(template)
            ? prefix
            : $"{prefix.TrimEnd('/')}/{template.TrimStart('/')}";

        return Normalize(combined);
    }

    private static string Normalize(string route) => "/" + route.Trim('/');
}
