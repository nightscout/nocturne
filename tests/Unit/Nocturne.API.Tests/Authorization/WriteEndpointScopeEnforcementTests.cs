using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Nocturne.API.Attributes;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the per-action authorization invariant for the V1/V2/V3 (Nightscout-compat) plane:
/// every write endpoint (POST/PUT/PATCH/DELETE) must carry a <see cref="RequireScopeAttribute"/>
/// or a <see cref="RequirePermissionAttribute"/> (e.g. [RequireAdmin]) so that a read-only or
/// public (unauthenticated) grant cannot create, update, or delete data.
///
/// Without this, the controller-level <c>HasPermissions</c> policy only checks that the
/// permission set is non-empty, which a public read grant satisfies — letting anonymous
/// callers write. This test fails if a new write endpoint ships without per-action authorization.
/// </summary>
public class WriteEndpointScopeEnforcementTests
{
    private static readonly string[] WriteVerbs = ["POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// Controllers exempt from the per-action RequireScope rule, with justification.
    /// </summary>
    private static readonly HashSet<string> ExemptControllers = new()
    {
        // Alexa performs its own read-permission check and writes no tenant data; it is a
        // read operation exposed over POST and must remain usable on public tenants.
        "AlexaController",
    };

    [Fact]
    public void EveryV1AndV3WriteAction_RequiresAnOAuthScope()
    {
        var assembly = typeof(RequireScopeAttribute).Assembly;
        var violations = new List<string>();
        var writeActionsChecked = 0;

        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace is not ("Nocturne.API.Controllers.V1"
                or "Nocturne.API.Controllers.V2"
                or "Nocturne.API.Controllers.V3"))
                continue;
            if (type.IsAbstract || !typeof(ControllerBase).IsAssignableFrom(type))
                continue;
            if (ExemptControllers.Contains(type.Name))
                continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var verbs = method.GetCustomAttributes<HttpMethodAttribute>()
                    .SelectMany(a => a.HttpMethods)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!verbs.Overlaps(WriteVerbs))
                    continue;

                // Endpoints deliberately marked [AllowAnonymous] (e.g. external webhook
                // callbacks verified by other means) are intentionally public.
                if (method.GetCustomAttribute<AllowAnonymousAttribute>() != null)
                    continue;

                writeActionsChecked++;

                var hasPerActionAuthz =
                    method.GetCustomAttribute<RequireScopeAttribute>() != null
                    || method.GetCustomAttribute<RequirePermissionAttribute>() != null
                    || type.GetCustomAttribute<RequireScopeAttribute>() != null
                    || type.GetCustomAttribute<RequirePermissionAttribute>() != null;

                if (!hasPerActionAuthz)
                    violations.Add($"{type.Name}.{method.Name} [{string.Join(",", verbs)}]");
            }
        }

        // Sanity: the scan must actually discover the write surface, otherwise the assertion
        // below would pass vacuously if the reflection query silently matched nothing.
        writeActionsChecked.Should().BeGreaterThan(30,
            "the reflection scan should discover the V1/V3 write endpoints");

        violations.Should().BeEmpty(
            "every V1/V2/V3 write endpoint must carry [RequireScope] or [RequirePermission] so a " +
            "read-only or public grant cannot create, update, or delete data. " +
            "Unprotected: " + string.Join("; ", violations));
    }
}
