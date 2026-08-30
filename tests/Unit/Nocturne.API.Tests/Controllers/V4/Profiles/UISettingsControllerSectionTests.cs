using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.Core.Models.Configuration;
using Xunit;
using static Nocturne.API.Tests.Controllers.V4.Profiles.UISettingsControllerHarness;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// Unit coverage for <c>GET ui-settings/{section}</c> and the demo-mode aggregate on
/// <see cref="UISettingsController"/>. Every <see cref="UISettingsSections"/> entry must be
/// addressable, and demo mode must inherit whatever it does not fixture itself.
/// </summary>
[Trait("Category", "Unit")]
public class UISettingsControllerSectionTests
{
    [Fact]
    public async Task GetSectionSettings_servesEverySectionTheAggregateOwns()
    {
        var controller = NewController();
        await controller.SaveUISettings(new UISettingsConfiguration());

        foreach (var section in UISettingsSections.All)
        {
            var result = await controller.GetSectionSettings(section.Name);

            result
                .Result.Should()
                .BeOfType<OkObjectResult>($"section {section.Name} should be addressable")
                .Which.Value.Should()
                .BeAssignableTo(section.Type);
        }
    }

    [Fact]
    public async Task GetSectionSettings_servesThePersistedDataQualitySection()
    {
        var controller = NewController();
        var settings = new UISettingsConfiguration();
        settings.DataQuality.SleepSchedule.BedtimeHour = 1;
        settings.DataQuality.SleepSchedule.Timezone = "Pacific/Auckland";
        settings.DataQuality.CompressionLowDetection.Enabled = false;
        await controller.SaveUISettings(settings);

        var dataQuality = OkValue<DataQualitySettings>(
            (await controller.GetSectionSettings("dataQuality")).Result
        );

        dataQuality.SleepSchedule.BedtimeHour.Should().Be(1);
        dataQuality.SleepSchedule.Timezone.Should().Be("Pacific/Auckland");
        dataQuality.CompressionLowDetection.Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("Devices")]
    [InlineData("DEVICES")]
    [InlineData("dataquality")]
    [InlineData("DATAQUALITY")]
    [InlineData("DataQuality")]
    public async Task GetSectionSettings_matchesSectionNamesWithoutRegardToCase(string section)
    {
        var controller = NewController();

        var result = await controller.GetSectionSettings(section);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetSectionSettings_reports404_forASectionTheAggregateDoesNotOwn()
    {
        var controller = NewController();

        var result = await controller.GetSectionSettings("therapy");

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetUISettings_inDemoMode_inheritsEverySectionItDoesNotFixture()
    {
        var controller = NewController(demoMode: true);

        var settings = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);

        settings.Features.Widgets.Should().BeEquivalentTo(new FeatureSettings().Widgets);
        settings.Features.Display.Should().BeEquivalentTo(new DisplaySettings());
        settings.Algorithm.Should().BeEquivalentTo(new AlgorithmSettings());
        settings.Notifications.Should().BeEquivalentTo(new NotificationSettings());
        settings.DataQuality.Should().BeEquivalentTo(new DataQualitySettings());
        settings.Services.SyncSettings.Should().BeEquivalentTo(new SyncSettings());
    }

    [Fact]
    public async Task GetUISettings_inDemoMode_keepsItsSampleDevicesServicesAndPlugins()
    {
        var controller = NewController(demoMode: true);

        var settings = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);

        settings.Devices.ConnectedDevices.Should().NotBeEmpty();
        settings.Services.ConnectedServices.Should().NotBeEmpty();
        settings.Features.Plugins.Should().ContainKey("delta");
    }
}
