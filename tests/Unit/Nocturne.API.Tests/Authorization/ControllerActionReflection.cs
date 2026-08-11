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
/// Reflection over the API's MVC controllers, shared by the authorization guard tests.
/// </summary>
internal static class ControllerActionReflection
{
    public static Assembly ApiAssembly => typeof(AuthorizationConfiguration).Assembly;

    public static IEnumerable<Type> GetControllers() =>
        ApiAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t)
                        && t is { IsClass: true, IsAbstract: false, IsPublic: true }
                        && t.GetCustomAttribute<NonControllerAttribute>() is null);

    public static IEnumerable<MethodInfo> GetActionMethods(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName
                        && m.GetCustomAttributes().OfType<IActionHttpMethodProvider>().Any());

    /// <summary>
    /// The absolute routes an action answers on, with a leading slash and no trailing one.
    /// </summary>
    /// <remarks>Attribute routing only; the API declares no conventional routes.</remarks>
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

    public static bool HasAnonymous(MethodInfo action, Type controller) =>
        action.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any()
        || controller.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any();

    public static bool HasAuthorizationGate(MethodInfo action, Type controller) =>
        HasGate(action.GetCustomAttributes(inherit: true))
        || HasGate(controller.GetCustomAttributes(inherit: true));

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
