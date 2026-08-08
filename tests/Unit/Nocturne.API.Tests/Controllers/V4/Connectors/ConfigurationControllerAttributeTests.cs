using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Nocturne.API.Controllers.V4.Connectors;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Connectors;

/// <summary>
/// Guards the authorization attributes on <see cref="ConfigurationController"/>. The effective
/// configuration endpoint returns saved non-secret connector values, which include account
/// identifiers such as the connector account username or email. It must require authentication:
/// the class-level <c>[Authorize]</c> gates it, so a per-action <c>[AllowAnonymous]</c> would
/// return those identifiers to any unauthenticated caller.
/// </summary>
public class ConfigurationControllerAttributeTests
{
    [Fact]
    public void GetEffectiveConfiguration_IsNotAllowAnonymous()
    {
        var method = typeof(ConfigurationController)
            .GetMethod(nameof(ConfigurationController.GetEffectiveConfiguration));

        Assert.NotNull(method);
        Assert.Null(method!.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void Controller_RequiresAuthorization()
    {
        var authorize = typeof(ConfigurationController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
    }
}
