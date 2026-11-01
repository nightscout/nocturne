using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;
using static Nocturne.API.Tests.Controllers.V4.Profiles.UISettingsControllerHarness;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// Unit coverage for <c>PATCH features</c>, <c>PATCH dataQuality</c> and <c>PATCH notifications</c>.
/// Each merges a patch into the one row its section owns, so two patches started from the same
/// snapshot cannot undo one another.
/// </summary>
[Trait("Category", "Unit")]
public class UISettingsControllerSectionPatchTests
{
    [Fact]
    public async Task PatchesOfDifferentFields_BothSurvive()
    {
        var controller = NewController();

        await controller.PatchFeatures("""{"display":{"focusHours":6}}""");
        await controller.PatchFeatures("""{"trackerPills":{"enabled":false}}""");

        var stored = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);
        stored.Features.Display.FocusHours.Should().Be(6);
        stored.Features.TrackerPills.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task PatchesOfDifferentSections_BothSurvive()
    {
        var controller = NewController();

        await controller.PatchFeatures("""{"display":{"focusHours":9}}""");
        await controller.PatchDataQuality("""{"sleepSchedule":{"bedtimeHour":1}}""");

        var stored = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);
        stored.Features.Display.FocusHours.Should().Be(9);
        stored.DataQuality.SleepSchedule.BedtimeHour.Should().Be(1);
    }

    [Fact]
    public async Task APatch_ReturnsTheMergedSection()
    {
        var controller = NewController();
        await controller.PatchDataQuality("""{"sleepSchedule":{"bedtimeHour":1}}""");

        var merged = OkValue<DataQualitySettings>(
            (await controller.PatchDataQuality("""{"sleepSchedule":{"wakeTimeHour":9}}""")).Result
        );

        merged.SleepSchedule.BedtimeHour.Should().Be(1);
        merged.SleepSchedule.WakeTimeHour.Should().Be(9);
    }

    [Fact]
    public async Task PatchFeatureSettings_WritesOnlyTheFeaturesRow()
    {
        var database = NewDatabase();
        var controller = NewController(database);
        await controller.PatchNotifications("""{"alarmConfiguration":{"globalVolume":10}}""");
        await controller.PatchDataQuality("""{"sleepSchedule":{"bedtimeHour":2}}""");
        var before = await Rows(database);

        await controller.PatchFeatures("""{"display":{"focusHours":6}}""");

        var after = await Rows(database);
        after.Keys.Except(before.Keys).Should().BeEquivalentTo(["ui:settings:features"]);
        foreach (var (key, value) in before)
        {
            after[key].Should().Be(value, $"{key} is not the features row");
        }
    }

    [Fact]
    public async Task PatchNotificationSettings_MergesIntoTheAlarmConfigurationTheAlarmRoutesServe()
    {
        var controller = NewController();
        await controller.SaveAlarmConfiguration(
            new UserAlarmConfiguration
            {
                Profiles = [new AlarmProfileConfiguration { Id = "low", Threshold = 70 }],
            }
        );

        var merged = OkValue<NotificationSettings>(
            (await controller.PatchNotifications("""{"alarmConfiguration":{"globalVolume":10}}""")).Result
        );

        merged.AlarmConfiguration.GlobalVolume.Should().Be(10);
        merged.AlarmConfiguration.Profiles.Should().ContainSingle().Which.Id.Should().Be("low");
        var served = OkValue<UserAlarmConfiguration>((await controller.GetAlarmConfiguration()).Result);
        served.GlobalVolume.Should().Be(10);
        served.Profiles.Should().ContainSingle();
    }

    [Fact]
    public async Task AnAbsentKeyIsUntouchedAndAPresentDefaultValuedKeyIsWritten()
    {
        var controller = NewController();
        await controller.PatchFeatures("""{"display":{"focusHours":6},"trackerPills":{"enabled":false}}""");

        var merged = OkValue<FeatureSettings>(
            (await controller.PatchFeatures("""{"trackerPills":{"enabled":true}}""")).Result
        );

        merged.Display.FocusHours.Should().Be(6, "the patch does not name it, and the section default is 12");
        merged.TrackerPills.Enabled.Should().BeTrue("a key set to its default is still a key");
    }

    [Fact]
    public async Task APresentKeyHoldingTheZeroValueIsWritten()
    {
        var controller = NewController();
        await controller.PatchDataQuality("""{"sleepSchedule":{"bedtimeHour":23,"wakeTimeHour":7}}""");

        var merged = OkValue<DataQualitySettings>(
            (await controller.PatchDataQuality("""{"sleepSchedule":{"bedtimeHour":0}}""")).Result
        );

        merged.SleepSchedule.BedtimeHour.Should().Be(0);
        merged.SleepSchedule.WakeTimeHour.Should().Be(7);
    }

    [Theory]
    [InlineData("""{"Display":{"FocusHours":6}}""")]
    [InlineData("""{"display":{"FocusHours":6}}""")]
    [InlineData("""{"display":{"focusHours":6},"Display":{"focusHours":9}}""")]
    [InlineData("""{"display":{"nightMode":true,"noSuchField":1}}""")]
    [InlineData("""{"noSuchSection":{"enabled":true}}""")]
    public async Task APatchKeyThatIsNotAPropertyNameOfTheSection_IsRefusedAndNothingIsWritten(string body)
    {
        var database = NewDatabase();
        var controller = NewController(database);

        var result = await controller.PatchFeatures(body);

        StatusOf(result.Result).Should().Be(StatusCodes.Status400BadRequest);
        (await Rows(database)).Should().BeEmpty();
    }

    [Fact]
    public async Task APatchKeyInsideAListItem_IsCheckedToo()
    {
        var controller = NewController();

        var result = await controller.PatchNotifications(
            """{"alarmConfiguration":{"profiles":[{"Id":"low"}]}}""");

        StatusOf(result.Result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ACamelCaseKeyNestedInTheSection_IsApplied()
    {
        var controller = NewController();

        var result = await controller.PatchNotifications(
            """{"alarmConfiguration":{"profiles":[{"id":"low","threshold":70}],"globalVolume":10}}""");

        var merged = OkValue<NotificationSettings>(result.Result);
        merged.AlarmConfiguration.GlobalVolume.Should().Be(10);
        merged.AlarmConfiguration.Profiles.Should().ContainSingle().Which.Threshold.Should().Be(70);
    }

    [Theory]
    [InlineData("""{"display":{"focusHours":"soon"}}""")]
    [InlineData("not json")]
    public async Task AStoredRowThatDoesNotRead_ReportsAServerFaultForAnOrdinaryPatch(string stored)
    {
        var database = NewDatabase();
        database.Settings.Add(
            new SettingsEntity
            {
                Id = Guid.CreateVersion7(),
                Key = "ui:settings:features",
                Value = stored,
                IsActive = true,
            });
        await database.SaveChangesAsync();

        var result = await NewController(database).PatchFeatures("""{"trackerPills":{"enabled":false}}""");

        StatusOf(result.Result).Should().Be(StatusCodes.Status500InternalServerError);
    }

    private static int? StatusOf(IActionResult? result) => (result as ObjectResult)?.StatusCode;

    private static async Task<Dictionary<string, string?>> Rows(NocturneDbContext db) =>
        await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
}
