using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Xunit;
using static Nocturne.API.Tests.Controllers.V4.Profiles.UISettingsControllerHarness;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// Unit coverage for <c>PUT features</c> and <c>PUT dataQuality</c>. Each writes the one row its
/// section owns, so two saves started from the same snapshot cannot overwrite one another.
/// </summary>
[Trait("Category", "Unit")]
public class UISettingsControllerSectionSaveTests
{
    [Fact]
    public async Task SavesFromOneSnapshot_ToDifferentSections_BothSurvive()
    {
        var controller = NewController();
        var snapshot = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);

        var features = snapshot.Features;
        features.Display.FocusHours = 9;
        var dataQuality = snapshot.DataQuality;
        dataQuality.SleepSchedule.BedtimeHour = 1;

        OkValue<FeatureSettings>((await controller.SaveFeatureSettings(features)).Result);
        OkValue<DataQualitySettings>((await controller.SaveDataQualitySettings(dataQuality)).Result);

        var stored = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);
        stored.Features.Display.FocusHours.Should().Be(9);
        stored.DataQuality.SleepSchedule.BedtimeHour.Should().Be(1);
    }

    [Fact]
    public async Task SaveFeatureSettings_LeavesEveryOtherRowUntouched()
    {
        var database = NewDatabase();
        var controller = NewController(database);
        await controller.SaveNotificationSettings(
            new NotificationSettings
            {
                AlarmConfiguration = new UserAlarmConfiguration
                {
                    Profiles = [new AlarmProfileConfiguration { Id = "low", Threshold = 70 }],
                },
            }
        );
        await controller.SaveDataQualitySettings(
            new DataQualitySettings { SleepSchedule = { BedtimeHour = 2 } }
        );
        var before = await Rows(database);

        await controller.SaveFeatureSettings(new FeatureSettings { Display = { FocusHours = 6 } });

        var after = await Rows(database);
        after.Keys.Should().Contain("ui:settings:features");
        before.Keys.Should().Contain(["ui:settings:notifications", "ui:settings:notifications:alarms"]);
        foreach (var (key, value) in before)
        {
            after[key].Should().Be(value, $"{key} is not the features row");
        }
    }

    [Fact]
    public async Task SaveDataQualitySettings_WritesOnlyTheDataQualityRow()
    {
        var database = NewDatabase();
        var controller = NewController(database);
        await controller.SaveFeatureSettings(new FeatureSettings { Display = { FocusHours = 5 } });
        var before = await Rows(database);

        await controller.SaveDataQualitySettings(new DataQualitySettings());

        var after = await Rows(database);
        after.Keys.Except(before.Keys).Should().BeEquivalentTo(["ui:settings:dataquality"]);
        after["ui:settings:features"].Should().Be(before["ui:settings:features"]);
    }

    private static async Task<Dictionary<string, string?>> Rows(NocturneDbContext db) =>
        await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
}
