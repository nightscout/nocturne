using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Which device-status values state the sensor's state, read as stored: a section of the extras
/// is a JSON object (<see cref="SensorStateReports"/>).
/// </summary>
[Trait("Category", "Unit")]
public class SensorStateReportsTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);

    private static readonly ReadingSource Source = new("carelink-connector", "CareLink MiniMed 780G");

    private static DeviceStatusExtras Status(string json, double minutesAgo = 0) => new()
    {
        Timestamp = Now.AddMinutes(-minutesAgo),
        CreatedAt = Now.AddMinutes(-minutesAgo),
        Extras = JsonSerializer.Deserialize<Dictionary<string, object?>>(json),
        Device = Source.Device,
        DataSource = Source.DataSource,
    };

    [Theory]
    [InlineData("NO_ERROR_MESSAGE", SensorState.Ok)]
    [InlineData("WARM_UP", SensorState.WarmingUp)]
    [InlineData("CALIBRATION_REQUIRED", SensorState.CalibrationNeeded)]
    [InlineData("SENSOR_ERROR", SensorState.Error)]
    [InlineData("CHANGE_SENSOR", SensorState.Error)]
    [InlineData("SENSOR_END_OF_LIFE", SensorState.Expired)]
    public void CareLink_states_its_allowlisted_values(string reported, SensorState expected) =>
        SensorStateReports.Of(Status($$$"""{"cgm": {"sensorState": "{{{reported}}}"}}""")).Should().Be(expected);

    [Theory]
    [InlineData(0x02, SensorState.WarmingUp)]
    [InlineData(0x06, SensorState.Ok)]
    [InlineData(0x0b, SensorState.Error)]
    [InlineData(0x0f, SensorState.Expired)]
    [InlineData(0x11, SensorState.Error)]
    public void Xdripjs_states_its_allowlisted_session_codes(int code, SensorState expected) =>
        SensorStateReports.Of(Status($$$"""{"xdripjs": {"state": {{{code}}}, "stateString": "x"}}""")).Should().Be(expected);

    [Theory]
    [InlineData("""{"cgm": {"sensorState": "SG_BELOW_40_MGDL"}}""")]
    [InlineData("""{"cgm": {"sensorState": "warm_up"}}""")]
    [InlineData("""{"cgm": {"sensorState": 3}}""")]
    [InlineData("""{"cgm": {"calibrationStatus": "DUE"}}""")]
    [InlineData("""{"xdripjs": {"state": 3, "stateString": "Excess Noise"}}""")]
    [InlineData("""{"xdripjs": {"state": 1, "stateString": "Stopped"}}""")]
    [InlineData("""{"xdripjs": {"state": 7, "stateString": "Needs Calibration"}}""")]
    [InlineData("""{"xdripjs": {"state": 14, "stateString": "Outlier Calibration Request"}}""")]
    [InlineData("""{"xdripjs": {"state": "6"}}""")]
    [InlineData("""{"xdripjs": {"stateString": "OK"}}""")]
    [InlineData("""{"cgm": "WARM_UP"}""")]
    [InlineData("""{"radioAdapter": {"RSSI": -60}}""")]
    public void Any_other_value_states_nothing(string json) =>
        SensorStateReports.Of(Status(json)).Should().BeNull();

    [Fact]
    public void The_newest_status_that_states_one_decides()
    {
        var statuses = new[]
        {
            Status("""{"cgm": {"sensorState": "WARM_UP"}}""", minutesAgo: 8),
            Status("""{"cgm": {"sensorState": "NO_ERROR_MESSAGE"}}""", minutesAgo: 3),
            Status("""{"radioAdapter": {"RSSI": -60}}""", minutesAgo: 1),
        };

        SensorStateReports.Newest(statuses, Now, Source).Should().Be((SensorState.Ok, Now.AddMinutes(8)));
    }

    [Fact]
    public void A_status_past_the_bound_or_after_now_states_nothing()
    {
        var statuses = new[]
        {
            Status("""{"cgm": {"sensorState": "WARM_UP"}}""", minutesAgo: 11.01),
            Status("""{"cgm": {"sensorState": "WARM_UP"}}""", minutesAgo: -1),
        };

        SensorStateReports.Newest(statuses, Now, Source).Should().BeNull("one arrived too long ago, one is not stored yet");
        SensorStateReports.Newest([Status("""{"cgm": {"sensorState": "WARM_UP"}}""", minutesAgo: 11)], Now, Source)
            .Should().Be((SensorState.WarmingUp, Now), "a status exactly the bound old still states its state");
    }

    [Fact]
    public void A_status_matches_only_the_device_and_source_it_was_written_under()
    {
        var status = Status("""{"cgm": {"sensorState": "SENSOR_ERROR"}}""");

        SensorStateReports.Describes(status, Source).Should().BeTrue();
        SensorStateReports.Describes(status, Source with { Device = "CareLink Guardian" }).Should().BeFalse();
        SensorStateReports.Describes(status, Source with { DataSource = "nightscout" }).Should().BeFalse();
        SensorStateReports.Describes(status, null).Should().BeFalse();
        SensorStateReports.Describes(new DeviceStatusExtras { Device = null, DataSource = Source.DataSource }, Source with { Device = null })
            .Should().BeFalse("a device missing on both sides is not a match");
    }
}
