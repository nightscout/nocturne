using System.Text;
using System.Text.Json;
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
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// A <see cref="UISettingsController"/> wired to a real <see cref="UISettingsService"/> over an
/// in-memory database, so every write can be asserted through the read that serves it. The caller
/// holds full access unless a test names its scopes.
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
            new UISettingsService(dbContext.AsFactory(), TimeProvider.System, NullLogger<UISettingsService>.Instance),
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
        IHttpClientFactory httpClientFactory,
        params string[] grantedScopes
    )
    {
        var services = new ServiceCollection();
        services.AddControllers();

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(
            grantedScopes.Length == 0 ? [Scope.FullAccess] : grantedScopes);

        return new UISettingsController(
            NullLogger<UISettingsController>.Instance,
            demoMode,
            new DemoUISettingsProxy(
                demoMode,
                httpClientFactory,
                TimeProvider.System,
                NullLogger<DemoUISettingsProxy>.Instance
            ),
            settingsService
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    internal static Task<ActionResult<FeatureSettings>> PatchFeatures(
        this UISettingsController controller,
        string body
    ) => controller.PatchFeatureSettings(Bound<FeatureSettings>(controller, body));

    internal static Task<ActionResult<DataQualitySettings>> PatchDataQuality(
        this UISettingsController controller,
        string body
    ) => controller.PatchDataQualitySettings(Bound<DataQualitySettings>(controller, body));

    internal static Task<ActionResult<NotificationSettings>> PatchNotifications(
        this UISettingsController controller,
        string body
    ) => controller.PatchNotificationSettings(Bound<NotificationSettings>(controller, body));

    /// <summary>
    /// What the MVC pipeline hands a PATCH action: the body bound to the section type, and the
    /// raw body left readable on the request.
    /// </summary>
    private static T Bound<T>(UISettingsController controller, string body)
    {
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.Request.ContentType = "application/json";
        return JsonSerializer.Deserialize<T>(body)!;
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
