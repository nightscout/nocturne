using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// What the newest stored <see cref="SensorStateReport"/> states of the sensor
/// (<see cref="SensorStateReports"/>). Which report is the newest, and when it arrived, is the
/// repository's (DeviceStatusBatchReadsTests); what a status's values map to is the decomposer's
/// (DeviceStatusDecomposerTests, sensor state).
/// </summary>
[Trait("Category", "Unit")]
public class SensorStateReportsTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);

    private static ArrivedSensorStateReport Newest(SensorState? state, double minutesAgo = 0) =>
        new(new SensorStateReport { State = state }, Now.AddMinutes(-minutesAgo));

    [Fact]
    public void A_report_states_its_state_until_the_bound_after_it_arrived()
    {
        SensorStateReports.StateOf(Newest(SensorState.Ok, minutesAgo: 3)).Should().Be((SensorState.Ok, Now.AddMinutes(8)));
    }

    [Fact]
    public void A_newest_report_with_no_state_states_nothing()
    {
        SensorStateReports.StateOf(Newest(null)).Should().BeNull("an older state must not speak for the sensor after it");
        SensorStateReports.StateOf(null).Should().BeNull();
    }
}
