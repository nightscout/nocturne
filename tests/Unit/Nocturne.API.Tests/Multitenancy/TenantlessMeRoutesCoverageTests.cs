using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Tests.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// A route added to a controller the apex already serves is refused there unless it also gets an
/// allowlist entry, and nothing else fails: the action never runs. A controller counts as served
/// at the apex when any of its routes under <c>/api/v4/me</c> is tenantless-allowed, so each of
/// its other routes must be too, or be named in <see cref="NeedsATenant"/> with the reason.
/// </summary>
[Trait("Category", "Unit")]
public sealed partial class TenantlessMeRoutesCoverageTests
{
    private const string MeRoutes = "/api/v4/me";

    private static bool IsMeRoute(string route) =>
        route.Equals(MeRoutes, StringComparison.OrdinalIgnoreCase)
        || route.StartsWith(MeRoutes + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Routes of an apex-served controller that stay tenant-gated, keyed by method and route template.</summary>
    private static readonly IReadOnlyDictionary<(string Method, string Route), string> NeedsATenant =
        new Dictionary<(string Method, string Route), string>
        {
            [("POST", "/api/v4/me/tenants")] =
                "creates a tenant, which has no place on a host that resolves none; the hosted deployment goes through billing",
        };

    private static readonly IReadOnlyList<MeRoute> Routes =
        (from controller in ControllerActionReflection.GetControllers()
         from action in ControllerActionReflection.GetActionMethods(controller)
         from route in ControllerActionReflection.GetRoutes(controller, action)
         where IsMeRoute(route)
         let methods = ControllerActionReflection.GetHttpMethods(action).ToList()
         from method in methods.Count == 0 ? [AnyMethod] : methods
         select new MeRoute(controller.Name, method, route))
        .ToList();

    private static readonly IReadOnlyList<string> ServedAtApex =
        Routes.Where(r => r.IsTenantless).Select(r => r.Controller).Distinct().Order().ToList();

    [Fact]
    public void EveryRouteOfAnApexServedControllerIsTenantlessOrSaysWhyNot()
    {
        var missing = Routes
            .Where(r => ServedAtApex.Contains(r.Controller) && !r.IsTenantless)
            .Where(r => !NeedsATenant.ContainsKey((r.Method, r.Template)))
            .Select(r => $"{r.Controller}: {r.Method} {r.Template}")
            .Order(StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "a controller the apex serves refuses its other routes there unless TenantResolutionMiddleware "
            + "lists them, as an exact entry naming the method; list each one, or name it in NeedsATenant "
            + "with why it needs a tenant. Missing:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void EveryRouteNamedAsNeedingATenantIsRoutedAndStillRefusedWithoutOne()
    {
        foreach (var ((method, template), reason) in NeedsATenant)
        {
            var route = Routes.SingleOrDefault(r => r.Method == method && r.Template == template);

            route.Should().NotBeNull($"{method} {template} is named as needing a tenant but no action answers it");
            route!.IsTenantless.Should().BeFalse(
                $"{method} {template} is named as needing a tenant ({reason}) but the allowlist admits it");
        }
    }

    [Fact]
    public void TheDiscoveryFindsTheApexServedControllers()
    {
        ServedAtApex.Should().Contain(
            [nameof(Nocturne.API.Controllers.V4.Identity.MyTenantsController), nameof(Nocturne.API.Controllers.V4.Identity.TeamsController)],
            "these are the controllers the apex's People page reads through");
        Routes.Should().Contain(r => r.Template == "/api/v4/me/tenants/overview/groups" && r.Method == "GET");
    }

    private sealed record MeRoute(string Controller, string Method, string Template)
    {
        public bool IsTenantless =>
            TenantResolutionMiddleware.IsTenantlessAllowed(ParameterSegment().Replace(Template, SampleId), Method);
    }

    /// <summary>An action with no method constraint answers every method; listed under this name so it is not dropped.</summary>
    private const string AnyMethod = "ANY";

    private const string SampleId = "0195f0a0-0000-7000-8000-000000000001";

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex ParameterSegment();
}
