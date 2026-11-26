using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Migration;
using Xunit;

namespace Nocturne.API.Tests.Services.Migration;

[Trait("Category", "Unit")]
public class NightscoutAlarmSettingsTests
{
    // Thresholds are mg/dL in status.json whatever DISPLAY_UNITS says.
    private const string MmolSiteStatus = """
        {"status":"ok","name":"nightscout","version":"15.0.2","serverTime":"2026-10-30T01:02:03.000Z",
         "settings":{"units":"mmol","timeFormat":12,"alarmUrgentHigh":true,"alarmHigh":false,
           "alarmLow":true,"alarmUrgentLow":false,"alarmTimeagoWarn":true,"alarmTimeagoWarnMins":15,
           "thresholds":{"bgHigh":260,"bgTargetTop":180,"bgTargetBottom":72,"bgLow":54}}}
        """;

    [Fact]
    public void ReadsTheFourThresholdsAndTheFiveSwitches()
    {
        NightscoutAlarmSettings.Parse(MmolSiteStatus).Should().Be(new NightscoutAlarmSettings(
            BgLow: 54, BgTargetBottom: 72, BgTargetTop: 180, BgHigh: 260,
            AlarmUrgentLow: false, AlarmLow: true, AlarmHigh: false, AlarmUrgentHigh: true, AlarmTimeagoWarn: true));
    }

    [Fact]
    public void ReadsAFractionalThreshold()
    {
        NightscoutAlarmSettings.Parse("""{"settings":{"thresholds":{"bgLow":54.5}}}""").BgLow.Should().Be(54.5m);
    }

    [Theory]
    [InlineData("""{"status":"ok"}""")]
    [InlineData("""{"settings":null}""")]
    [InlineData("""{"settings":[]}""")]
    [InlineData("""{"settings":{}}""")]
    [InlineData("""{"settings":{"thresholds":null}}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ReadsNothingFromABodyWithNoSettings(string body)
    {
        NightscoutAlarmSettings.Parse(body)
            .Should().Be(new NightscoutAlarmSettings(null, null, null, null, null, null, null, null, null));
    }

    [Fact]
    public void ReadsANumberAndASwitchSentAsStrings()
    {
        const string body = """
            {"settings":{"alarmUrgentLow":"on","alarmLow":"true","alarmHigh":"false","alarmUrgentHigh":1,"alarmTimeagoWarn":"0",
              "thresholds":{"bgLow":"55","bgTargetBottom":"72.5","bgTargetTop":180,"bgHigh":"260"}}}
            """;

        NightscoutAlarmSettings.Parse(body).Should().Be(new NightscoutAlarmSettings(
            BgLow: 55, BgTargetBottom: 72.5m, BgTargetTop: 180, BgHigh: 260,
            AlarmUrgentLow: true, AlarmLow: true, AlarmHigh: false, AlarmUrgentHigh: true, AlarmTimeagoWarn: false));
    }

    [Fact]
    public void LeavesOutAValueThatCannotBeRead()
    {
        const string body = """
            {"settings":{"alarmUrgentLow":"maybe","alarmLow":null,"alarmHigh":2,"alarmTimeagoWarn":true,
              "thresholds":{"bgLow":"soon","bgTargetBottom":null,"bgTargetTop":true,"bgHigh":{}}}}
            """;

        NightscoutAlarmSettings.Parse(body).Should().Be(new NightscoutAlarmSettings(
            null, null, null, null,
            AlarmUrgentLow: null, AlarmLow: null, AlarmHigh: null, AlarmUrgentHigh: null, AlarmTimeagoWarn: true));
    }

    [Fact]
    public void RefusesABodyThatIsNotJson()
    {
        var parse = () => NightscoutAlarmSettings.Parse("<h1>STATUS OK</h1>");

        parse.Should().Throw<JsonException>();
    }

    [Fact]
    public void ARoundTripThroughTheStoredJsonKeepsEveryField()
    {
        var settings = NightscoutAlarmSettings.Parse(MmolSiteStatus);

        NightscoutAlarmSettings.FromJson(settings.ToJson()).Should().Be(settings);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    public void StoredJsonThatIsNotTheSettingsReadsAsNone(string stored)
    {
        NightscoutAlarmSettings.FromJson(stored).Should().BeNull();
    }
}
