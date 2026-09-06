using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Connectors.Tandem.Mappers;
using Nocturne.Connectors.Tandem.Models;
using Nocturne.Connectors.Tandem.Tests.EventParser;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.Mappers;

public class TandemProfileAndUserModeTests
{
    private const long Raw = 504921600L; // 2024-01-01T00:00:00Z
    private static readonly TandemTimeResolver Time = new(0);

    [Fact]
    public void ProfileMapper_builds_store_dropping_placeholder_segments()
    {
        var settings = new TandemPumpSettings
        {
            Profiles = new TandemPumpProfiles
            {
                ActiveIdp = 1,
                Profile =
                [
                    new TandemPumpProfile
                    {
                        Name = "Default",
                        Idp = 1,
                        InsulinDuration = 300, // -> DIA 5h
                        TDependentSegs =
                        [
                            new TandemPumpProfileSegment(), // all-zero placeholder -> skipped
                            new TandemPumpProfileSegment
                            {
                                StartTime = 360, BasalRate = 1000, Isf = 50, CarbRatio = 10000, TargetBg = 110,
                            },
                        ],
                    },
                ],
            },
            CgmSettings = new TandemPumpCgmSettings
            {
                LowGlucoseAlertMgPerDl = 70,
                HighGlucoseAlertMgPerDl = 180,
            },
        };

        var profile = new TandemProfileMapper(NullLogger.Instance).Map(settings);

        profile.Should().NotBeNull();
        profile!.DefaultProfile.Should().Be("Default");
        var data = profile.Store["Default"];
        data.Dia.Should().Be(5);
        data.Basal.Should().ContainSingle();
        data.Basal[0].Time.Should().Be("06:00");
        data.Basal[0].Value.Should().Be(1.0); // 1000 mU -> 1.0 U
        data.CarbRatio.Should().ContainSingle().Which.Value.Should().Be(10); // 10000 -> 10 g/u
        data.Sens.Should().ContainSingle().Which.Value.Should().Be(50);
        data.TargetLow.Should().ContainSingle().Which.Value.Should().Be(70);
        data.TargetHigh.Should().ContainSingle().Which.Value.Should().Be(180);
    }

    [Fact]
    public void UserModeMapper_ignores_sleep_mode_events()
    {
        // Control-IQ Sleep-mode events no longer produce a span: StateSpanCategory.Sleep was
        // removed (sleep now lives in the sleep_sessions tables). A lone sleep start/stop pair
        // yields no state spans.
        var start = new TandemEventBuilder(229, Raw, seqNum: 1)
            .UInt8(1, 1)  // RequestedAction -> "Start Sleep"
            .UInt8(7, 1); // SleepStartedByGUI -> "TRUE"
        var stop = new TandemEventBuilder(229, Raw + 3600, seqNum: 2)
            .UInt8(1, 2); // RequestedAction -> "Stop Sleep"
        var events = TandemEventDecoder.Decode(TandemEventBuilder.ToBase64(start, stop));

        var spans = new TandemUserModeMapper(NullLogger.Instance, Time).Map(events);

        spans.Should().BeEmpty();
    }

    [Fact]
    public void UserModeMapper_leaves_unmatched_start_open()
    {
        var start = new TandemEventBuilder(229, Raw, seqNum: 1).UInt8(1, 3); // "Start Exercise"
        var spans = new TandemUserModeMapper(NullLogger.Instance, Time)
            .Map(TandemEventDecoder.Decode(TandemEventBuilder.ToBase64(start)));

        spans.Should().ContainSingle();
        spans[0].Category.Should().Be(StateSpanCategory.Exercise);
        spans[0].EndTimestamp.Should().BeNull();
    }
}
