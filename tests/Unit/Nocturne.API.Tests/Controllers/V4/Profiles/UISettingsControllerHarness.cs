using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
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

    internal static UISettingsController NewController(
        params (string Key, string Value)[] configuration
    )
    {
        return NewController(NewDatabase(), configuration);
    }

    internal static UISettingsController NewController(
        NocturneDbContext dbContext,
        params (string Key, string Value)[] configuration
    )
    {
        return NewController(
            new UISettingsService(dbContext, NullLogger<UISettingsService>.Instance),
            configuration
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
        params (string Key, string Value)[] configuration
    )
    {
        var services = new ServiceCollection();
        services.AddControllers();

        return new UISettingsController(
            NullLogger<UISettingsController>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    configuration.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value))
                )
                .Build(),
            Mock.Of<IHttpClientFactory>(),
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
