using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Which stored <see cref="SensorStateReport"/> states the sensor's state for a reading
/// (<see cref="SensorStateReports"/>). What a status's values map to is the decomposer's
/// (DeviceStatusDecomposerTests, sensor state).
/// </summary>
[Trait("Category", "Unit")]
public class SensorStateReportsTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);

    private static readonly ReadingSource Source = new("carelink-connector", "CareLink MiniMed 780G");

    private static SensorStateReport Report(SensorState? state, double minutesAgo = 0) => new()
    {
        Timestamp = Now.AddMinutes(-minutesAgo),
        CreatedAt = Now.AddMinutes(-minutesAgo),
        State = state,
        Device = Source.Device,
        DataSource = Source.DataSource,
    };

    [Fact]
    public void The_newest_report_decides()
    {
        var reports = new[]
        {
            Report(SensorState.WarmingUp, minutesAgo: 8),
            Report(SensorState.Ok, minutesAgo: 3),
        };

        SensorStateReports.Newest(reports, Now, Source).Should().Be((SensorState.Ok, Now.AddMinutes(8)));
    }

    [Fact]
    public void A_newest_report_whose_state_this_build_cannot_read_states_nothing()
    {
        var reports = new[]
        {
            Report(SensorState.Ok, minutesAgo: 3),
            Report(null, minutesAgo: 1),
        };

        SensorStateReports.Newest(reports, Now, Source).Should().BeNull("an older ok must not speak for the sensor after it");
    }

    [Fact]
    public void A_report_is_ordered_by_its_arrival_not_its_timestamp()
    {
        var aheadOfItsRow = Report(SensorState.Error, minutesAgo: 6);
        aheadOfItsRow.Timestamp = Now.AddSeconds(30);
        var newer = Report(SensorState.Ok, minutesAgo: 2);

        SensorStateReports.Newest([aheadOfItsRow, newer], Now, Source)
            .Should().Be((SensorState.Ok, Now.AddMinutes(9)));
    }

    [Fact]
    public void A_report_past_the_bound_or_after_now_states_nothing()
    {
        var reports = new[]
        {
            Report(SensorState.WarmingUp, minutesAgo: 11.01),
            Report(SensorState.WarmingUp, minutesAgo: -1),
        };

        SensorStateReports.Newest(reports, Now, Source).Should().BeNull("one arrived too long ago, one is not stored yet");
        SensorStateReports.Newest([Report(SensorState.WarmingUp, minutesAgo: 11)], Now, Source)
            .Should().Be((SensorState.WarmingUp, Now), "a report exactly the bound old still states its state");
    }

    [Fact]
    public void A_report_matches_only_the_device_and_source_it_was_written_under()
    {
        var report = Report(SensorState.Error);

        SensorStateReports.Describes(report, Source).Should().BeTrue();
        SensorStateReports.Describes(report, Source with { Device = "CareLink Guardian" }).Should().BeFalse();
        SensorStateReports.Describes(report, Source with { DataSource = "nightscout" }).Should().BeFalse();
        SensorStateReports.Describes(report, null).Should().BeFalse();
        SensorStateReports.Describes(new SensorStateReport { Device = null, DataSource = Source.DataSource }, Source with { Device = null })
            .Should().BeFalse("a device missing on both sides is not a match");
        SensorStateReports.Newest([Report(SensorState.Error)], Now, Source with { DataSource = "nightscout" })
            .Should().BeNull("another uploader's report of the same sensor does not win the match");
    }
}
