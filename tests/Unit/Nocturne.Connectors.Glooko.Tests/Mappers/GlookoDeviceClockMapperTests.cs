using FluentAssertions;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.Timezones;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

[Trait("Category", "Unit")]
public class GlookoDeviceClockMapperTests
{
    private const string Connector = "glooko";

    // ── Offset notation ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("+10:00", 600)]
    [InlineData("-04:00", -240)]
    [InlineData("+05:30", 330)]
    [InlineData("+00:00", 0)]
    [InlineData("-09:30", -570)]
    public void ParseOffsetMinutes_ReadsGlookoNotation(string text, int expected) =>
        GlookoDeviceClockMapper.ParseOffsetMinutes(text).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("+10")]
    [InlineData("10:00:00")]
    public void ParseOffsetMinutes_RejectsAnythingElse(string? text) =>
        GlookoDeviceClockMapper.ParseOffsetMinutes(text).Should().BeNull();

    // ── Profile observation gates ────────────────────────────────────────────

    private static GlookoSsv2User User(
        string? timezone = "America/New_York",
        string? utcOffset = "-04:00",
        string? updatedAt = "2026-04-10T12:00:00.000Z",
        string? updatedBy = "client",
        bool softDeleted = false) => new()
    {
        Guid = "user-guid",
        Timezone = timezone,
        UtcOffset = utcOffset,
        UpdatedAt = updatedAt,
        UpdatedBy = updatedBy,
        SoftDeleted = softDeleted,
    };

    [Fact]
    public void ValidProfile_BecomesAnEstimateAtItsOwnUpdateTime()
    {
        var obs = GlookoDeviceClockMapper.MapProfileObservation([User()], Connector);

        obs.Should().NotBeNull();
        obs!.Source.Should().Be(DeviceClockObservationSource.Profile);
        obs.OffsetMinutes.Should().Be(-240);
        obs.IsEstimate.Should().BeTrue();
        // Staleness needs no cutoff: the observation is dated by the record's own updatedAt.
        obs.ObservedAtUtc.Should().Be(new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc));
        obs.DeclaredTimezone.Should().Be("America/New_York");
    }

    [Fact]
    public void NeverSetAccount_AssertsNothing()
    {
        // The server placeholder: no declared zone, zero offset. Seeding UTC from it would shift the
        // tenant's entire history by their true offset.
        GlookoDeviceClockMapper.MapProfileObservation(
            [User(timezone: null, utcOffset: "+00:00", updatedBy: "server")], Connector)
            .Should().BeNull();
    }

    [Fact]
    public void ZeroOffsetWithNoZone_IsTreatedAsNeverSet_WhoeverWroteIt()
    {
        GlookoDeviceClockMapper.MapProfileObservation(
            [User(timezone: "", utcOffset: "+00:00", updatedBy: "client")], Connector)
            .Should().BeNull();
    }

    [Fact]
    public void ZeroOffsetWithADeclaredZone_IsARealClaim()
    {
        // Iceland runs on UTC year-round; a declared zone makes the zero offset meaningful.
        GlookoDeviceClockMapper.MapProfileObservation(
            [User(timezone: "Atlantic/Reykjavik", utcOffset: "+00:00")], Connector)
            .Should().NotBeNull();
    }

    [Fact]
    public void MissingUpdatedAt_OrOffset_AssertsNothing()
    {
        GlookoDeviceClockMapper.MapProfileObservation([User(updatedAt: null)], Connector).Should().BeNull();
        GlookoDeviceClockMapper.MapProfileObservation([User(utcOffset: null)], Connector).Should().BeNull();
    }

    [Fact]
    public void SoftDeletedUsers_AreSkipped()
    {
        GlookoDeviceClockMapper.MapProfileObservation([User(softDeleted: true)], Connector).Should().BeNull();
    }

    [Fact]
    public void EmptyPage_AssertsNothing()
    {
        GlookoDeviceClockMapper.MapProfileObservation(null, Connector).Should().BeNull();
        GlookoDeviceClockMapper.MapProfileObservation([], Connector).Should().BeNull();
    }

    // ── Upload batch mapping ─────────────────────────────────────────────────

    [Fact]
    public void CgmRecords_MapToBatchObservations_ExcludingCalculatedAndDeleted()
    {
        var egvs = new[]
        {
            new GlookoClockEgv { DisplayTime = "2026-04-10T14:00:00.000Z", SyncTimestamp = "2026-04-10T12:00:00.000Z" },
            new GlookoClockEgv { DisplayTime = "2026-04-10T13:55:00.000Z", SyncTimestamp = "2026-04-10T12:00:00.000Z" },
            new GlookoClockEgv { DisplayTime = "2026-04-10T13:50:00.000Z", SyncTimestamp = "2026-04-10T12:00:00.000Z", Calculated = true },
            new GlookoClockEgv { DisplayTime = "2026-04-10T13:45:00.000Z", SyncTimestamp = "2026-04-10T12:00:00.000Z", SoftDeleted = true },
        };

        var obs = GlookoDeviceClockMapper.MapUploadBatches(Connector, egvs, null);

        var single = obs.Should().ContainSingle().Subject;
        single.SampleCount.Should().Be(2);
        single.OffsetMinutes.Should().Be(120); // sparse after filtering → bound only
        single.IsEstimate.Should().BeFalse();
    }

    [Fact]
    public void BolusRecords_PreferPumpTimestamp_AndFallBackToTimestamp()
    {
        var boluses = new[]
        {
            new GlookoClockBolus { PumpTimestamp = "2026-04-10T14:00:00.000Z", SyncTimestamp = "2026-04-10T12:00:00.000Z" },
            new GlookoClockBolus { PumpTimestamp = null, Timestamp = "2026-04-11T14:00:00.000Z", SyncTimestamp = "2026-04-11T12:00:00.000Z" },
        };

        var obs = GlookoDeviceClockMapper.MapUploadBatches(Connector, null, boluses);

        obs.Should().HaveCount(2);
        obs.Should().OnlyContain(o => o.OffsetMinutes == 120);
    }

    [Fact]
    public void FeedsAreEstimatedSeparately_SoSparseBolusesCannotDiluteADenseCgmBatch()
    {
        var sync = "2026-04-10T12:00:00.000Z";
        var egvs = Enumerable.Range(0, 8)
            .Select(i => new GlookoClockEgv
            {
                DisplayTime = $"2026-04-10T{13}:{55 - 5 * i:00}:00.000Z",
                SyncTimestamp = sync,
            })
            .ToArray();
        var boluses = new[]
        {
            new GlookoClockBolus { PumpTimestamp = "2026-04-10T08:00:00.000Z", SyncTimestamp = sync },
        };

        var obs = GlookoDeviceClockMapper.MapUploadBatches(Connector, egvs, boluses);

        obs.Should().HaveCount(2);
        obs.Count(o => o.IsEstimate).Should().Be(1); // the CGM batch alone earns the estimate
    }

    [Fact]
    public void UnparseableRecords_AreDropped()
    {
        var egvs = new[]
        {
            new GlookoClockEgv { DisplayTime = "not a date", SyncTimestamp = "2026-04-10T12:00:00.000Z" },
            new GlookoClockEgv { DisplayTime = "2026-04-10T14:00:00.000Z", SyncTimestamp = null },
        };

        GlookoDeviceClockMapper.MapUploadBatches(Connector, egvs, null).Should().BeEmpty();
    }
}
