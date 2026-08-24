using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Tests.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// Every tenantless entry must name an endpoint that is actually served, under a method it is
/// actually served on. An entry nothing answers reads as permission while granting none: the
/// paths a caller does ask for stay tenant-gated, and the 404 or <c>503 setup_required</c> they
/// get back appears to come from a path the list plainly admits.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantlessAllowedPathsCoverageTests
{
    /// <summary>
    /// Entries served outside MVC routing, which <see cref="ControllerActionReflection"/> cannot
    /// see, keyed by path with where each is mapped.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NonControllerEntries =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/health"] = "MapHealthChecks in Nocturne.Aspire.ServiceDefaults",
            ["/alive"] = "MapHealthChecks in Nocturne.Aspire.ServiceDefaults",
            ["/hubs/overview"] = "MapHub<OverviewHub> in Program.cs",
        };

    private static readonly IReadOnlyList<(string Route, string Method)> RoutedEndpoints =
        (from controller in ControllerActionReflection.GetControllers()
         from action in ControllerActionReflection.GetActionMethods(controller)
         from route in ControllerActionReflection.GetRoutes(controller, action)
         from method in ControllerActionReflection.GetHttpMethods(action)
         select (route, method))
        .Distinct()
        .ToList();

    [Fact]
    public void EveryTenantlessEntryNamesARoutedEndpoint()
    {
        var dead = TenantResolutionMiddleware.TenantlessPaths
            .Where(entry => !NonControllerEntries.ContainsKey(entry.Path))
            .Where(entry => !RoutedEndpoints.Any(e => entry.Matches(e.Route, e.Method)))
            .Select(Describe)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        dead.Should().BeEmpty(
            "a tenantless entry no route answers admits nothing, while making the paths a caller "
            + "does ask for look permitted — point each at the route it meant, or drop it. Dead:\n  "
            + string.Join("\n  ", dead));
    }

    /// <summary>
    /// Staleness on the hand-kept list: an entry named there and then removed from the middleware
    /// would leave a name the guard silently skips.
    /// </summary>
    [Fact]
    public void EveryNonControllerEntryIsStillOnTheAllowList()
    {
        var paths = TenantResolutionMiddleware.TenantlessPaths
            .Select(p => p.Path)
            .ToList();

        foreach (var (path, mappedAt) in NonControllerEntries)
        {
            paths.Should().Contain(
                p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase),
                "{0} is skipped as mapped by {1} but is no longer tenantless-allowed — drop the entry",
                path, mappedAt);
        }
    }

    /// <summary>
    /// Non-vacuity: a route enumeration that returns nothing passes the guard above only because
    /// every entry would then be reported, so pin that it finds the surface and that an entry is
    /// proven by a discovered route rather than by the skip list.
    /// </summary>
    [Fact]
    public void TheRouteDiscoveryFindsTheControllerSurface()
    {
        RoutedEndpoints.Should().Contain(("/api/v4/me/tenants", "GET"),
            "the subject's tenant list is served off the apex and is the entry whose exact-match "
            + "shape this guard exists to keep honest");

        TenantResolutionMiddleware.TenantlessPaths
            .Count(entry => RoutedEndpoints.Any(e => entry.Matches(e.Route, e.Method)))
            .Should().BeGreaterThan(NonControllerEntries.Count,
                "most of the list is served by controllers, so a run where only the skip list "
                + "resolves means route matching has stopped working");
    }

    private static string Describe(TenantResolutionMiddleware.TenantlessPath entry) =>
        entry.Path
        + (entry.Prefix ? " (prefix)" : " (exact)")
        + (entry.Method is null ? "" : $" {entry.Method}");
}
