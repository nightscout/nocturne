using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Xt;
using Nocturne.Connectors.Glooko.Tests.Xt;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Xt;

public class GlookoXtExportMapperTests
{
    private readonly GlookoXtExportMapper _mapper = new(NullLogger.Instance);
    private readonly GlookoXtExport _export = GlookoXtExportParser.Parse(GlookoXtExportParserTests.SampleCsv);

    [Fact]
    public void PumpModes_BecomeSpansWithTheirDuration()
    {
        var batch = _mapper.Map(_export, "mgdl");

        var modes = batch.StateSpans.Where(s => s.OriginalId!.Contains("_mode_")).ToList();
        modes.Should().HaveCount(2);
        var attempting = modes.Single(s => s.State == PumpModeState.Limited.ToString());
        attempting.EndTimestamp.Should().Be(attempting.StartTimestamp.AddMilliseconds(103_000));
        attempting.Category.Should().Be(StateSpanCategory.PumpMode);
        attempting.Metadata!["label"].Should().Be("Attempting");
        modes.Single(s => s.State == PumpModeState.Automatic.ToString()).Metadata!["label"].Should().Be("Closed loop");
    }

    [Fact]
    public void BoostAndEaseOff_AreOverrides_NotPumpModes()
    {
        var batch = _mapper.Map(_export, "mgdl");

        var overrides = batch.StateSpans.Where(s => s.Category == StateSpanCategory.Override).ToList();
        overrides.Should().HaveCount(2);
        overrides.Should().OnlyContain(s => s.State == OverrideState.Custom.ToString());
        overrides.Select(s => (string)s.Metadata!["name"]).Should().BeEquivalentTo(["Boost", "Ease-off"]);
        var boost = overrides.Single(s => (string)s.Metadata!["name"] == "Boost");
        boost.EndTimestamp.Should().Be(boost.StartTimestamp.AddHours(1));
        batch.StateSpans.Where(s => s.Category == StateSpanCategory.PumpMode).Should().OnlyContain(s => s.State == "Automatic" || s.State == "Limited" || s.State == "Manual");
    }

    [Theory]
    [InlineData("Closed loop", PumpModeState.Automatic)]
    [InlineData("Attempting", PumpModeState.Limited)]
    [InlineData("Manual", PumpModeState.Manual)]
    [InlineData("Open loop", PumpModeState.Manual)]
    [InlineData("Suspended", PumpModeState.Suspended)]
    [InlineData("Off", PumpModeState.Off)]
    [InlineData("Pining", PumpModeState.Limited)]
    public void ModeState_NamesEveryStateThePumpReports(string label, PumpModeState expected)
    {
        GlookoXtExportMapper.ModeState(label).Should().Be(expected);
    }

    [Fact]
    public void SafetyAlert_BecomesACgmHazardWithTheReading()
    {
        var batch = _mapper.Map(_export, "mgdl");

        var alert = batch.SystemEvents.Single(e => e.Code == "safety_low");
        alert.EventType.Should().Be(SystemEventType.Hazard);
        alert.Category.Should().Be(SystemEventCategory.Cgm);
        alert.Metadata!["mgdl"].Should().Be(69.0);
    }

    [Fact]
    public void OtherAlarm_IsUnclassifiedInfo_AndOcclusionIsLeftToTheRecordStream()
    {
        var batch = _mapper.Map(_export, "mgdl");

        var other = batch.SystemEvents.Should().ContainSingle(e => e.Code == "AlarmOther").Subject;
        other.Category.Should().Be(SystemEventCategory.Pump);
        other.EventType.Should().Be(SystemEventType.Info, "Glooko XT keeps no detail for it, so it cannot warn of anything specific");
        other.Description.Should().Contain("not reported");
        batch.SystemEvents.Should().NotContain(e => e.Code == "AlarmOcclusion");
    }

    [Fact]
    public void ReservoirChange_IsADeviceEvent_AndDailyTotalsAreIgnored()
    {
        var batch = _mapper.Map(_export, "mgdl");

        batch.DeviceEvents.Should().ContainSingle().Which.EventType.Should().Be(DeviceEventType.ReservoirChange);
        batch.Notes.Should().BeEmpty();
    }

    [Fact]
    public void PumpSettings_BecomeAProfile()
    {
        var batch = _mapper.Map(_export, "mgdl");

        var profile = batch.Profiles.Should().ContainSingle().Subject;
        profile.Id.Should().Be("glookoxt_settings_1789814335895");
        profile.DefaultProfile.Should().Be("1");
        profile.IsExternallyManaged.Should().BeTrue();
        profile.EnteredBy.Should().Be("mylife YpsoPump");
        var data = profile.Store["1"];
        data.Dia.Should().Be(2.5);
        data.Basal.Select(b => (b.Time, b.Value)).Should().Equal([("00:00", 0.8), ("08:00", 0.7)]);
        data.CarbRatio.Single().Value.Should().Be(10);
        data.Sens.Single().Value.Should().Be(40);
        data.TargetLow.Single().Value.Should().Be(70, "the pump's own band outranks the single target");
        data.TargetHigh.Single().Value.Should().Be(180);
    }

    [Fact]
    public void TimeBetweenLoopSpans_IsManual()
    {
        var t0 = new DateTime(2026, 9, 6, 10, 0, 0, DateTimeKind.Utc);
        var export = new GlookoXtExport
        {
            Rows =
            [
                new GlookoXtExportRow { TimestampUtc = t0, Event = "pumpMode - Closed loop", DurationMs = 3_600_000 },
                new GlookoXtExportRow { TimestampUtc = t0.AddHours(3), Event = "pumpMode - Attempting", DurationMs = 120_000 },
                new GlookoXtExportRow { TimestampUtc = t0.AddHours(3).AddMinutes(2), Event = "pumpMode - Closed loop", DurationMs = 3_600_000 },
                new GlookoXtExportRow { TimestampUtc = t0.AddHours(1), Event = "pumpSettingsOverride - boost", DurationMs = 3_600_000 },
            ],
        };

        var batch = _mapper.Map(export, "mgdl");

        var manual = batch.StateSpans.Where(s => s.State == PumpModeState.Manual.ToString()).ToList();
        manual.Should().ContainSingle("only the two-hour hole between loop spans is manual; the minute-rounded join and the overlapping boost are not");
        manual[0].StartTimestamp.Should().Be(t0.AddHours(1));
        manual[0].EndTimestamp.Should().Be(t0.AddHours(3));
        manual[0].Metadata!["inferred"].Should().Be(true);
    }

    [Fact]
    public void ARowSeenTwice_IsMappedOnce()
    {
        var twice = new GlookoXtExport { Rows = [.. _export.Rows, .. _export.Rows] };

        var once = _mapper.Map(_export, "mgdl");
        var doubled = _mapper.Map(twice, "mgdl");

        doubled.Total.Should().Be(once.Total);
    }
}
