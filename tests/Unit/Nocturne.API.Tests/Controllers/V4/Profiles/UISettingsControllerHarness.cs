using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// A <see cref="UISettingsController"/> wired to a real <see cref="UISettingsService"/> over an
/// in-memory database, so every write can be asserted through the read that serves it.
/// </summary>
internal static class UISettingsControllerHarness
{
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    internal static UISettingsController NewController(bool demoMode = false)
    {
        return NewController(NewDatabase(), demoMode);
    }

    internal static UISettingsController NewController(
        NocturneDbContext dbContext,
        bool demoMode = false
    )
    {
        return NewController(
            new UISettingsService(dbContext, NullLogger<UISettingsService>.Instance),
            demoMode
        );
    }

    /// <summary>
    /// An empty tenant database, for tests that seed settings rows the service's own writers cannot
    /// produce.
    /// </summary>
    internal static NocturneDbContext NewDatabase()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new NocturneDbContext(options) { TenantId = TenantId };
    }

    internal static UISettingsController NewController(
        IUISettingsService settingsService,
        bool demoMode = false
    )
    {
        return NewController(settingsService, DemoMode(demoMode), Mock.Of<IHttpClientFactory>());
    }

    /// <summary>
    /// Demo mode as <see cref="IDemoModeService"/> reports it. Without a service URL the controller
    /// has nothing to proxy to and serves its own demo fixtures.
    /// </summary>
    internal static IDemoModeService DemoMode(bool enabled, string? serviceUrl = null)
    {
        var demoMode = new Mock<IDemoModeService>();
        demoMode.SetupGet(d => d.IsEnabled).Returns(enabled);
        demoMode
            .SetupGet(d => d.IsConfigured)
            .Returns(enabled && !string.IsNullOrWhiteSpace(serviceUrl));
        demoMode.SetupGet(d => d.ServiceUrl).Returns(serviceUrl);

        return demoMode.Object;
    }

    internal static UISettingsController NewController(
        IUISettingsService settingsService,
        IDemoModeService demoMode,
        IHttpClientFactory httpClientFactory
    )
    {
        var services = new ServiceCollection();
        services.AddControllers();

        return new UISettingsController(
            NullLogger<UISettingsController>.Instance,
            demoMode,
            httpClientFactory,
            settingsService
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services.BuildServiceProvider(),
                },
            },
        };
    }

    /// <summary>
    /// The body of a 200 response, asserting both the status and the body type.
    /// </summary>
    internal static T OkValue<T>(ActionResult? result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;

        return ok.Value.Should().BeAssignableTo<T>().Subject;
    }
}
