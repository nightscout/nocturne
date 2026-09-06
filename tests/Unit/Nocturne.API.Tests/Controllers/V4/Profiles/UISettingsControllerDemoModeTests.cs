using System.Diagnostics;
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
        settings.DataQuality.SleepSchedule.Timezone = "Pacific/Auckland";

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
        var demoService = new StubDemoService(HttpStatusCode.OK, ProxiedSettings);

        var settings = await ProxiedGet(demoService);

        settings.Devices.ConnectedDevices.Should().ContainSingle().Which.Id.Should()
            .Be(ProxiedDeviceId);
        demoService.RequestUri.Should().Be("http://demo-service/ui-settings");
    }

    [Fact]
    public async Task GetUISettings_inDemoMode_normalisesATrailingSlashOnTheServiceUrl()
    {
        var demoService = new StubDemoService(HttpStatusCode.OK, ProxiedSettings);

        await ProxiedGet(demoService, "http://demo-service/");

        demoService.RequestUri.Should().Be("http://demo-service/ui-settings");
    }

    [Fact]
    public async Task GetUISettings_inDemoMode_fallsBackToItsOwnFixtures_whenTheDemoServiceFails()
    {
        var settings = await ProxiedGet(new StubDemoService(HttpStatusCode.InternalServerError));

        settings.Devices.ConnectedDevices.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetUISettings_inDemoMode_fallsBackToItsOwnFixtures_whenTheDemoServiceFailsWithAReadableBody()
    {
        var settings = await ProxiedGet(
            new StubDemoService(HttpStatusCode.InternalServerError, ProxiedSettings)
        );

        settings.Devices.ConnectedDevices.Should().NotBeEmpty();
        settings.Devices.ConnectedDevices.Select(d => d.Id).Should().NotContain(ProxiedDeviceId);
    }

    [Fact]
    public async Task GetUISettings_inDemoMode_stopsWaitingOnAHungDemoService()
    {
        var demoService = StubDemoService.Hanging();
        var elapsed = Stopwatch.StartNew();

        var settings = await ProxiedGet(demoService);
        elapsed.Stop();

        settings.Devices.ConnectedDevices.Should().NotBeEmpty();
        demoService.Cancelled.Should().BeTrue();
        elapsed.Elapsed.Should().BeLessThan(UISettingsController.DemoServiceProxyTimeout * 4);
    }

    [Fact]
    public async Task GetUISettings_outsideDemoMode_neverCallsTheDemoService()
    {
        var demoService = new StubDemoService(HttpStatusCode.OK, ProxiedSettings);
        var controller = NewController(
            new UISettingsService(NewDatabase(), NullLogger<UISettingsService>.Instance),
            DemoMode(enabled: false, serviceUrl: "http://demo-service"),
            demoService.Factory
        );

        await controller.GetUISettings();

        demoService.RequestUri.Should().BeNull();
    }

    private const string ProxiedDeviceId = "from-demo-service";

    private static UISettingsConfiguration ProxiedSettings =>
        new()
        {
            Devices = new DeviceSettings
            {
                ConnectedDevices = [new ConnectedDevice { Id = ProxiedDeviceId }],
            },
        };

    /// <summary>
    /// What <c>GET ui-settings</c> answers in demo mode with <paramref name="demoService"/> standing
    /// in for the external demo data service.
    /// </summary>
    private static async Task<UISettingsConfiguration> ProxiedGet(
        StubDemoService demoService,
        string serviceUrl = "http://demo-service"
    )
    {
        var controller = NewController(
            new UISettingsService(NewDatabase(), NullLogger<UISettingsService>.Instance),
            DemoMode(enabled: true, serviceUrl: serviceUrl),
            demoService.Factory
        );

        return OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);
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
        private readonly bool _hangs;

        internal StubDemoService(HttpStatusCode status, UISettingsConfiguration? settings = null)
        {
            _status = status;
            _settings = settings;
        }

        private StubDemoService(bool hangs)
            : this(HttpStatusCode.OK)
        {
            _hangs = hangs;
        }

        /// <summary>
        /// A demo service that never answers, so only the caller's own deadline ends the call.
        /// </summary>
        internal static StubDemoService Hanging()
        {
            return new StubDemoService(hangs: true);
        }

        internal string? RequestUri { get; private set; }

        internal bool Cancelled { get; private set; }

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
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken
            )
            {
                owner.RequestUri = request.RequestUri?.ToString();

                if (owner._hangs)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        owner.Cancelled = true;
                        throw;
                    }
                }

                return new HttpResponseMessage(owner._status)
                {
                    Content = owner._settings == null ? null : JsonContent.Create(owner._settings),
                };
            }
        }
    }
}
