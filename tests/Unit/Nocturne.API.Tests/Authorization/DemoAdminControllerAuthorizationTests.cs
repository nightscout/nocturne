using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Nocturne.API.Controllers.V4.Admin;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// <see cref="DemoAdminController"/> lives on the tenantless-allowed <c>/api/v4/admin/demo/</c>
/// prefix and its actions provision a tenant, grant the Public system subject the Admin role, and
/// bulk-delete the demo tenant's glucose, treatments, heart rate, steps, sleep and alert data.
/// It was <c>[AllowAnonymous]</c> while being reachable from the edge (the gateway sends
/// <c>/api/{**catch-all}</c> to nocturne-web, whose proxy forwards all of <c>/api</c> to the API),
/// so it is gated on the <c>platform_admin</c> role the instance key already yields.
/// </summary>
public class DemoAdminControllerAuthorizationTests
{
    [Fact]
    public void DemoAdminController_RequiresPlatformAdmin()
    {
        typeof(DemoAdminController)
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull("the demo lifecycle endpoints are destructive")
            .And.Subject.As<AuthorizeAttribute>()
            .Roles.Should().Be("platform_admin");
    }

    [Fact]
    public void DemoAdminController_IsNotAnonymous()
    {
        typeof(DemoAdminController)
            .GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().BeNull();

        typeof(DemoAdminController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() != null)
            .Should().BeEmpty("a per-action opt-out would reopen the anonymous path");
    }
}
