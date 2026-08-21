using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;
using static Nocturne.API.Tests.Controllers.V4.Profiles.UISettingsControllerHarness;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// Unit coverage for the per-profile alarm routes on <see cref="UISettingsController"/>. Each write
/// is asserted through the matching read: the profile list these routes edit is the same
/// <see cref="UserAlarmConfiguration"/> blob <c>GET notifications/alarms</c> returns.
/// </summary>
[Trait("Category", "Unit")]
public class UISettingsControllerAlarmProfileTests
{
    [Fact]
    public async Task AddOrUpdateAlarmProfile_isVisibleToGetAlarmConfiguration()
    {
        var controller = NewController();

        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration
        {
            Id = "night-high",
            Name = "Nighttime High",
            AlarmType = AlarmTriggerType.High,
            Threshold = 220,
        });

        var profiles = await StoredProfiles(controller);
        profiles.Should().ContainSingle(p => p.Id == "night-high")
            .Which.Threshold.Should().Be(220);
    }

    [Fact]
    public async Task AddOrUpdateAlarmProfile_replacesAnExistingProfileInPlace()
    {
        var controller = NewController();

        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration
        {
            Id = "night-high",
            Name = "Nighttime High",
            Threshold = 220,
        });
        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration
        {
            Id = "night-high",
            Name = "Nighttime High",
            Threshold = 190,
        });

        var profiles = await StoredProfiles(controller);
        profiles.Should().ContainSingle().Which.Threshold.Should().Be(190);
    }

    [Fact]
    public async Task AddOrUpdateAlarmProfile_assignsAnId_soBlankIdProfilesDoNotCollide()
    {
        var controller = NewController();

        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration
        {
            Id = string.Empty,
            Name = "First",
        });
        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration
        {
            Id = string.Empty,
            Name = "Second",
        });

        var profiles = await StoredProfiles(controller);
        profiles.Should().HaveCount(2);
        profiles.Select(p => p.Id).Should().OnlyHaveUniqueItems().And.NotContain(string.Empty);
    }

    [Fact]
    public async Task DeleteAlarmProfile_removesItFromGetAlarmConfiguration()
    {
        var controller = NewController();

        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration { Id = "keep" });
        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration { Id = "drop" });

        await controller.DeleteAlarmProfile("drop");

        (await StoredProfiles(controller)).Select(p => p.Id).Should().Equal("keep");
    }

    [Fact]
    public async Task DeleteAlarmProfile_reports404_forAnUnknownProfile()
    {
        var controller = NewController();

        var result = await controller.DeleteAlarmProfile("never-existed");

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// An explicit JSON null binds over the property initialiser, so these rows deserialize to a
    /// <see cref="NotificationSettings"/> with no alarm configuration at all. Earlier versions could
    /// write them; the read has to cope rather than treat it as a failed read.
    /// </summary>
    [Theory]
    [InlineData("ui:settings:complete", """{"notifications":{"alarmConfiguration":null}}""")]
    [InlineData("ui:settings:notifications", """{"alarmConfiguration":null}""")]
    public async Task GetAlarmConfiguration_servesAnEmptyConfiguration_forALegacyNullAlarmRow(
        string key,
        string value
    )
    {
        var database = NewDatabase();
        database.Settings.Add(
            new SettingsEntity
            {
                Id = Guid.CreateVersion7(),
                Key = key,
                Value = value,
                IsActive = true,
            }
        );
        await database.SaveChangesAsync();

        var result = await NewController(database).GetAlarmConfiguration();

        OkValue<UserAlarmConfiguration>(result.Result).Profiles.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAlarmConfiguration_reports500_whenTheStoredConfigurationCannotBeRead()
    {
        var controller = NewController(UnreadableAlarmConfiguration().Object);

        var result = await controller.GetAlarmConfiguration();

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task AddOrUpdateAlarmProfile_writesNothing_whenTheStoredConfigurationCannotBeRead()
    {
        var settingsService = UnreadableAlarmConfiguration();
        var controller = NewController(settingsService.Object);

        var result = await controller.AddOrUpdateAlarmProfile(
            new AlarmProfileConfiguration { Id = "night-high" });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        settingsService.Verify(
            s => s.SaveAlarmConfigurationAsync(
                It.IsAny<UserAlarmConfiguration>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteAlarmProfile_writesNothing_whenTheStoredConfigurationCannotBeRead()
    {
        var settingsService = UnreadableAlarmConfiguration();
        var controller = NewController(settingsService.Object);

        var result = await controller.DeleteAlarmProfile("night-high");

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        settingsService.Verify(
            s => s.SaveAlarmConfigurationAsync(
                It.IsAny<UserAlarmConfiguration>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ----- helpers -----

    private static async Task<List<AlarmProfileConfiguration>> StoredProfiles(
        UISettingsController controller
    )
    {
        var config = OkValue<UserAlarmConfiguration>(
            (await controller.GetAlarmConfiguration()).Result
        );

        return config.Profiles;
    }

    /// <summary>
    /// A service whose alarm configuration read fails, which is the only way
    /// <see cref="IUISettingsService.GetAlarmConfigurationAsync"/> yields null.
    /// </summary>
    private static Mock<IUISettingsService> UnreadableAlarmConfiguration()
    {
        var settingsService = new Mock<IUISettingsService>();
        settingsService
            .Setup(s => s.GetAlarmConfigurationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAlarmConfiguration?)null);

        return settingsService;
    }
}
