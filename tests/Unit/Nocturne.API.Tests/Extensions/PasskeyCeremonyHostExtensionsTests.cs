using Fido2NetLib;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Extensions;

/// <summary>
/// Which host the ceremony guard judges, over a real <see cref="PasskeyService"/>.
/// </summary>
public class PasskeyCeremonyHostExtensionsTests
{
    private sealed class StubController : ControllerBase;

    private static StubController ControllerOn(HostString host)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = host;

        return new StubController
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static PasskeyService ServiceFor(string rpId)
    {
        var fido2Config = new Fido2Configuration
        {
            ServerDomain = rpId,
            ServerName = "Test",
            Origins = new HashSet<string> { $"https://{rpId}" },
        };

        return new PasskeyService(
            TestDbContextFactory.CreateInMemoryContext(),
            new Fido2(fido2Config),
            new EphemeralDataProtectionProvider(),
            Options.Create(fido2Config),
            NullLogger<PasskeyService>.Instance,
            Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PortBearingHost_IsAdmitted()
    {
        // The rp.id is a bare host, so the port has to come off before the comparison. An edge
        // published on a non-default port would otherwise refuse every ceremony it serves.
        var controller = ControllerOn(new HostString("cgm.example.com", 8443));

        controller.PasskeyHostRefusal(ServiceFor("cgm.example.com")).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HostElsewhere_IsRefused()
    {
        var controller = ControllerOn(new HostString("cgm.example.com", 8443));

        controller.PasskeyHostRefusal(ServiceFor("other.example.com")).Should().NotBeNull();
    }
}
