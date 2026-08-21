using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Models.Configuration;
using Xunit;
using static Nocturne.API.Tests.Controllers.V4.Profiles.UISettingsControllerHarness;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// Demo-mode coverage for <see cref="UISettingsController"/>. Demo mode is whatever
/// <see cref="Nocturne.API.Services.Platform.IDemoModeService"/> says it is, and while it is on the
/// controller serves fixtures and persists nothing.
/// </summary>
[Trait("Category", "Unit")]
public class UISettingsControllerDemoModeTests
{
    [Fact]
    public async Task SaveUISettings_inDemoMode_persistsNothing()
    {
        var settings = new UISettingsConfiguration();
        settings.Security.RequireAuthForPublicAccess = true;

        await PersistsNothing(c => c.SaveUISettings(settings));
    }

    [Fact]
    public async Task SaveNotificationSettings_inDemoMode_persistsNothing()
    {
        await PersistsNothing(c => c.SaveNotificationSettings(new NotificationSettings()));
    }

    [Fact]
    public async Task SaveAlarmConfiguration_inDemoMode_persistsNothing()
    {
        await PersistsNothing(c => c.SaveAlarmConfiguration(new UserAlarmConfiguration()));
    }

    [Fact]
    public async Task AddOrUpdateAlarmProfile_inDemoMode_persistsNothing()
    {
        await PersistsNothing(c => c.AddOrUpdateAlarmProfile(Profile()));
    }

    [Fact]
    public async Task DeleteAlarmProfile_inDemoMode_persistsNothing()
    {
        await PersistsNothing(c => c.DeleteAlarmProfile(Profile().Id));
    }

    [Fact]
    public async Task GetAlarmConfiguration_inDemoMode_servesTheSampleProfiles()
    {
        var controller = NewController(demoMode: true);

        var config = OkValue<UserAlarmConfiguration>(
            (await controller.GetAlarmConfiguration()).Result
        );

        config.Profiles.Should().NotBeEmpty();
        config.Profiles.Select(p => p.AlarmType).Should().Contain(AlarmTriggerType.UrgentLow);
    }

    [Fact]
    public async Task GetUISettings_inDemoMode_servesWhatTheDemoServiceReturns()
    {
        var proxied = new UISettingsConfiguration
        {
            Devices = new DeviceSettings
            {
                ConnectedDevices = [new ConnectedDevice { Id = "from-demo-service" }],
            },
        };
        var demoService = new StubDemoService(proxied);
        var controller = NewController(
            new UISettingsService(NewDatabase(), NullLogger<UISettingsService>.Instance),
            DemoMode(enabled: true, serviceUrl: "http://demo-service"),
            demoService.Factory
        );

        var settings = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);

        settings.Devices.ConnectedDevices.Should().ContainSingle().Which.Id.Should()
            .Be("from-demo-service");
        demoService.RequestUri.Should().Be("http://demo-service/ui-settings");
    }

    [Fact]
    public async Task GetUISettings_inDemoMode_fallsBackToItsOwnFixtures_whenTheDemoServiceFails()
    {
        var demoService = new StubDemoService(HttpStatusCode.InternalServerError);
        var controller = NewController(
            new UISettingsService(NewDatabase(), NullLogger<UISettingsService>.Instance),
            DemoMode(enabled: true, serviceUrl: "http://demo-service"),
            demoService.Factory
        );

        var settings = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);

        settings.Devices.ConnectedDevices.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetUISettings_outsideDemoMode_neverCallsTheDemoService()
    {
        var demoService = new StubDemoService(new UISettingsConfiguration());
        var controller = NewController(
            new UISettingsService(NewDatabase(), NullLogger<UISettingsService>.Instance),
            DemoMode(enabled: false, serviceUrl: "http://demo-service"),
            demoService.Factory
        );

        await controller.GetUISettings();

        demoService.RequestUri.Should().BeNull();
    }

    /// <summary>
    /// A write endpoint answers 200 in demo mode without leaving a settings row behind.
    /// </summary>
    private static async Task PersistsNothing<T>(
        Func<UISettingsController, Task<ActionResult<T>>> write
    )
    {
        var database = NewDatabase();

        var result = await write(NewController(database, demoMode: true));

        result.Result.Should().BeOfType<OkObjectResult>();
        database.Settings.ToList().Should().BeEmpty();
    }

    private static AlarmProfileConfiguration Profile()
    {
        return new AlarmProfileConfiguration
        {
            Id = "demo-profile",
            Name = "Demo",
            AlarmType = AlarmTriggerType.Low,
            Threshold = 70,
        };
    }

    /// <summary>
    /// Stands in for the external demo data service, recording the URL the controller asked for.
    /// </summary>
    private sealed class StubDemoService
    {
        private readonly UISettingsConfiguration? _settings;
        private readonly HttpStatusCode _status;

        internal StubDemoService(UISettingsConfiguration settings)
        {
            _settings = settings;
            _status = HttpStatusCode.OK;
        }

        internal StubDemoService(HttpStatusCode status)
        {
            _settings = null;
            _status = status;
        }

        internal string? RequestUri { get; private set; }

        internal IHttpClientFactory Factory
        {
            get
            {
                var factory = new Mock<IHttpClientFactory>();
                factory
                    .Setup(f => f.CreateClient(It.IsAny<string>()))
                    .Returns(() => new HttpClient(new Handler(this)));

                return factory.Object;
            }
        }

        private sealed class Handler(StubDemoService owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken
            )
            {
                owner.RequestUri = request.RequestUri?.ToString();

                return Task.FromResult(
                    new HttpResponseMessage(owner._status)
                    {
                        Content =
                            owner._settings == null ? null : JsonContent.Create(owner._settings),
                    }
                );
            }
        }
    }
}
