using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Covers <see cref="GlookoSettingsProfileMapper"/> — the SSV2 <c>pumps/settings</c> → Nocturne Profile
/// mapping (segment seconds-of-day conversion, mg/dL × 100 scaling, DIA-in-seconds, current-profile
/// selection, soft-delete skip), the SSV2-native replacement for the v3 devices_and_settings profile source.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoSettingsProfileMapperTests
{
    private const string ConnectorSource = "glooko-connector";
    private readonly GlookoSettingsProfileMapper _mapper;

    public GlookoSettingsProfileMapperTests()
    {
        _mapper = new GlookoSettingsProfileMapper(ConnectorSource, Mock.Of<ILogger>());
    }

    /// <summary>Builds a settings record matching the live shape from the task brief.</summary>
    private static GlookoSsv2PumpSettings Sample(
        string guid = "f6b48506-0000-0000-0000-000000000000",
        string timestamp = "2025-05-12T03:19:49.859Z",
        bool softDeleted = false,
        double activeInsulinTime = 10800,
        bool basalCurrent = true,
        bool bolusCurrent = true) => new()
    {
        Guid = guid,
        PumpTimestamp = timestamp,
        SoftDeleted = softDeleted,
        ActiveInsulinTime = activeInsulinTime,
        BasalSettings =
        [
            new GlookoSsv2BasalSettings
            {
                IsCurrent = basalCurrent,
                ProfileId = "1",
                ProfileName = "1",
                Segments = [new GlookoSsv2BasalSegment { Start = 0, End = 86399, SegmentId = "0", Rate = 1 }],
            },
        ],
        BolusSettings =
        [
            new GlookoSsv2BolusSettings
            {
                Current = bolusCurrent,
                ProfileId = "Bolus Program",
                ProfileName = "Bolus Program",
                InsulinToCarbRatioSegments =
                    [new GlookoSsv2CarbRatioSegment { Start = 0, End = 86399, SegmentId = "0", InsulinToCarbsRatio = 20 }],
                IsfSegments =
                    [new GlookoSsv2IsfSegment { Start = 0, End = 86399, SegmentId = "0", InsulinSensitivityFactor = 9008 }],
                TargetBgSegments =
                    [new GlookoSsv2TargetBgSegment { Start = 0, End = 86399, SegmentId = "0", TargetBg = 10809 }],
            },
        ],
    };

    [Fact]
    public void TransformSettingsToProfile_MapsBasalCarbRatioIsfAndTarget()
    {
        var profile = _mapper.TransformSettingsToProfile([Sample()]);

        profile.Should().NotBeNull();
        profile!.Units.Should().Be("mg/dL");
        profile.EnteredBy.Should().Be("Glooko");
        profile.IsExternallyManaged.Should().BeTrue();
        profile.Store.Should().ContainKeys("1", "Bolus Program");

        var basal = profile.Store["1"].Basal;
        basal.Should().HaveCount(1);
        basal[0].Value.Should().Be(1);              // rate U/hr passed through
        basal[0].Time.Should().Be("00:00");         // start 0 sec → midnight
        basal[0].TimeAsSeconds.Should().Be(0);

        var bolus = profile.Store["Bolus Program"];
        bolus.CarbRatio.Should().ContainSingle().Which.Value.Should().Be(20);    // g/U passed through
        bolus.Sens.Should().ContainSingle().Which.Value.Should().Be(90.08);      // 9008 / 100
        bolus.TargetLow.Should().ContainSingle().Which.Value.Should().Be(108.09); // 10809 / 100
        bolus.TargetHigh.Should().ContainSingle().Which.Value.Should().Be(108.09);
    }

    [Fact]
    public void TransformSettingsToProfile_ConvertsSecondsOfDayToSegmentTimes()
    {
        var record = Sample();
        record.BasalSettings![0].Segments =
        [
            new GlookoSsv2BasalSegment { Start = 0, End = 23399, Rate = 1 },
            new GlookoSsv2BasalSegment { Start = 23400, End = 86399, Rate = 0.8 }, // 23400s = 06:30
        ];

        var profile = _mapper.TransformSettingsToProfile([record]);

        var basal = profile!.Store["1"].Basal;
        basal.Should().HaveCount(2);
        basal[1].Time.Should().Be("06:30");
        basal[1].TimeAsSeconds.Should().Be(23400);
        basal[1].Value.Should().Be(0.8);
    }

    [Fact]
    public void TransformSettingsToProfile_ConvertsDiaFromSeconds()
    {
        var profile = _mapper.TransformSettingsToProfile([Sample(activeInsulinTime: 10800)]);

        profile!.Store["1"].Dia.Should().Be(3.0);   // 10800s / 3600
    }

    [Fact]
    public void TransformSettingsToProfile_ZeroActiveInsulinTime_DefaultsDiaToThreeHours()
    {
        var profile = _mapper.TransformSettingsToProfile([Sample(activeInsulinTime: 0)]);

        profile!.Store["1"].Dia.Should().Be(3.0);
    }

    [Fact]
    public void TransformSettingsToProfile_PicksRecordWithCurrentProgram()
    {
        // Older record flagged current; newer record not current → the current one wins despite older ts.
        var current = Sample(guid: "current", timestamp: "2025-01-01T00:00:00.000Z", basalCurrent: true, bolusCurrent: true);
        current.BasalSettings![0].ProfileName = "ActiveProg";
        var notCurrent = Sample(guid: "stale", timestamp: "2025-09-09T00:00:00.000Z", basalCurrent: false, bolusCurrent: false);
        notCurrent.BasalSettings![0].ProfileName = "StaleProg";

        var profile = _mapper.TransformSettingsToProfile([notCurrent, current]);

        // Id uses the v3-compatible glooko_{mills} scheme; picking the "current" record is proven by the
        // ActiveProg store/default below and the 2025-01-01 StartDate (not the stale 2025-09-09 record).
        profile!.Id.Should().Be($"glooko_{profile.Mills}");
        profile.StartDate.Should().StartWith("2025-01-01");
        profile.Store.Should().ContainKey("ActiveProg");
        profile.DefaultProfile.Should().Be("ActiveProg");
    }

    [Fact]
    public void TransformSettingsToProfile_NoCurrentFlag_PicksLatestByTimestamp()
    {
        var older = Sample(guid: "older", timestamp: "2025-01-01T00:00:00.000Z", basalCurrent: false, bolusCurrent: false);
        var newer = Sample(guid: "newer", timestamp: "2025-09-09T00:00:00.000Z", basalCurrent: false, bolusCurrent: false);

        var profile = _mapper.TransformSettingsToProfile([older, newer]);

        profile!.Id.Should().Be($"glooko_{profile.Mills}");
        profile.StartDate.Should().StartWith("2025-09-09", "the latest-by-timestamp record is selected");
    }

    [Fact]
    public void TransformSettingsToProfile_SkipsSoftDeletedRecords()
    {
        var deleted = Sample(guid: "deleted", softDeleted: true);

        var profile = _mapper.TransformSettingsToProfile([deleted]);

        profile.Should().BeNull();
    }

    [Fact]
    public void TransformSettingsToProfile_DefaultProfileFromCurrentBasalProgram()
    {
        var record = Sample();
        record.BasalSettings![0].ProfileName = "Weekday";

        var profile = _mapper.TransformSettingsToProfile([record]);

        profile!.DefaultProfile.Should().Be("Weekday");
    }

    [Fact]
    public void TransformSettingsToProfile_StableId_MatchesV3Scheme()
    {
        var first = _mapper.TransformSettingsToProfile([Sample()]);
        var second = _mapper.TransformSettingsToProfile([Sample()]);

        // glooko_{mills} (same scheme as the v3 GlookoProfileMapper) so a v3↔SSV2 path switch upserts the
        // same Profile row rather than duplicating; deterministic across repeated syncs of one snapshot.
        first!.Id.Should().Be($"glooko_{first.Mills}").And.NotContain("settings");
        first.Id.Should().Be(second!.Id);
    }

    [Fact]
    public void TransformSettingsToProfile_NullTargetLowHigh_UsesSingleTargetForBoth()
    {
        var record = Sample();
        record.BolusSettings![0].TargetBgSegments =
        [
            new GlookoSsv2TargetBgSegment { Start = 0, End = 86399, TargetBg = 10809, TargetBgLow = null, TargetBgHigh = null },
        ];

        var profile = _mapper.TransformSettingsToProfile([record]);

        var bolus = profile!.Store["Bolus Program"];
        bolus.TargetLow[0].Value.Should().Be(108.09);
        bolus.TargetHigh[0].Value.Should().Be(108.09);
    }

    [Fact]
    public void TransformSettingsToProfile_EmptyList_ReturnsNull()
    {
        _mapper.TransformSettingsToProfile([]).Should().BeNull();
    }
}
