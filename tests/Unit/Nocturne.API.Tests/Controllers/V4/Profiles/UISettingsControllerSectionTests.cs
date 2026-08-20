using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.Core.Models.Configuration;
using Xunit;
using static Nocturne.API.Tests.Controllers.V4.Profiles.UISettingsControllerHarness;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// Unit coverage for <c>GET ui-settings/{section}</c> on <see cref="UISettingsController"/>: every
/// <see cref="UISettingsSections"/> entry must be addressable, and nothing else.
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

    [Fact]
    public async Task GetSectionSettings_servesThePersistedSecuritySection()
    {
        var controller = NewController();
        var settings = new UISettingsConfiguration();
        settings.Security.RequireAuthForPublicAccess = true;
        settings.Security.HideGlucoseInFavicon = true;
        await controller.SaveUISettings(settings);

        var security = OkValue<SecuritySettings>(
            (await controller.GetSectionSettings("security")).Result
        );

        security.RequireAuthForPublicAccess.Should().BeTrue();
        security.HideGlucoseInFavicon.Should().BeTrue();
    }

    [Fact]
    public async Task GetSectionSettings_reports404_forASectionTheAggregateDoesNotOwn()
    {
        var controller = NewController();

        var result = await controller.GetSectionSettings("therapy");

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }
}
