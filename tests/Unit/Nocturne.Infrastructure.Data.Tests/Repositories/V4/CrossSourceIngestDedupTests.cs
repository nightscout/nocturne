using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Source-less records, from an uploader posting to the v1 API or a Nightscout migration, against a
/// connector's copy of the same event. A v1 treatment create reaches the repositories one record
/// at a time, so the single-create path is exercised as well as the batch path.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Deduplication")]
public class CrossSourceIngestDedupTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string Connector = "tidepool-connector";
    private const string Manual = "manual";
    private static readonly DateTime EventTime = new(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly DeduplicationService _dedup;
    private readonly BolusRepository _boluses;
    private readonly CarbIntakeRepository _carbs;
    private readonly SensorGlucoseRepository _glucose;
    private readonly TempBasalRepository _tempBasals;
    private readonly NoteRepository _notes;

    public CrossSourceIngestDedupTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();
        _context.TenantId = TestTenantId;

        var dedupContext = _db.CreateContext();
        dedupContext.TenantId = TestTenantId;
        _dedup = new DeduplicationService(
            dedupContext, new Mock<IServiceScopeFactory>().Object, NullLogger<DeduplicationService>.Instance);

        var factory = new TestTenantDbContextFactory(_context);
        var audit = new Mock<IAuditContext>().Object;
        _boluses = new BolusRepository(factory, _dedup, audit, NullLogger<BolusRepository>.Instance);
        _carbs = new CarbIntakeRepository(factory, _dedup, audit, NullLogger<CarbIntakeRepository>.Instance);
        _glucose = new SensorGlucoseRepository(factory, _dedup, audit, NullLogger<SensorGlucoseRepository>.Instance);
        _tempBasals = new TempBasalRepository(factory, _dedup, audit, NullLogger<TempBasalRepository>.Instance);
        _notes = new NoteRepository(factory, _dedup, audit, NullLogger<NoteRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Bolus Bolus(double insulin, DateTime at, string? source, string? legacyId = null) => new()
    {
        Timestamp = at, Insulin = insulin, DataSource = source, LegacyId = legacyId, Device = source is null ? "Trio" : null,
    };

    private static CarbIntake Carb(double carbs, DateTime at, string? source, string? legacyId = null) => new()
    {
        Timestamp = at, Carbs = carbs, DataSource = source, LegacyId = legacyId, Device = source is null ? "Trio" : null,
    };

    private Task<IEnumerable<Bolus>> VisibleBolusesAsync() =>
        _boluses.GetAsync(EventTime.AddHours(-1), EventTime.AddHours(1), null, null);

    private Task<IEnumerable<CarbIntake>> VisibleCarbsAsync() =>
        _carbs.GetAsync(EventTime.AddHours(-1), EventTime.AddHours(1), null, null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectUploadBolus_AndConnectorCopy_ShowOneBolus(bool directUploadFirst)
    {
        var direct = Bolus(2.35, EventTime, source: null, legacyId: "syn-direct-bolus");
        var copy = Bolus(2.35, EventTime, Connector);

        if (directUploadFirst)
        {
            await _boluses.CreateAsync(direct, WriteOrigin.Live);
            await _boluses.BulkCreateAsync([copy], WriteOrigin.Live);
        }
        else
        {
            await _boluses.BulkCreateAsync([copy], WriteOrigin.Live);
            await _boluses.CreateAsync(direct, WriteOrigin.Live);
        }

        (await VisibleBolusesAsync()).Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectUploadCarb_AndConnectorCopy_ShowOneCarb(bool directUploadFirst)
    {
        var direct = Carb(24, EventTime, source: null, legacyId: "syn-direct-carb");
        var copy = Carb(24, EventTime, Connector);

        if (directUploadFirst)
        {
            await _carbs.CreateAsync(direct, WriteOrigin.Live);
            await _carbs.BulkCreateAsync([copy], WriteOrigin.Live);
        }
        else
        {
            await _carbs.BulkCreateAsync([copy], WriteOrigin.Live);
            await _carbs.CreateAsync(direct, WriteOrigin.Live);
        }

        (await VisibleCarbsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task DirectUploadGlucose_AndConnectorCopy_ShowOneReading()
    {
        await _glucose.CreateAsync(new SensorGlucose { Timestamp = EventTime, Mgdl = 142, Device = "Trio" }, WriteOrigin.Live);
        await _glucose.BulkCreateAsync(
            [new SensorGlucose { Timestamp = EventTime.AddSeconds(4), Mgdl = 142, DataSource = Connector }],
            WriteOrigin.Live);

        (await _glucose.GetAsync(EventTime.AddHours(-1), EventTime.AddHours(1), null, null))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task DirectUploadTempBasal_AndConnectorCopy_ShowOneTempBasal()
    {
        await _tempBasals.CreateAsync(new TempBasal
        {
            StartTimestamp = EventTime, EndTimestamp = EventTime.AddMinutes(30), Rate = 0.8, LegacyId = "syn-direct-tb",
        }, WriteOrigin.Live);
        await _tempBasals.BulkCreateAsync([new TempBasal
        {
            StartTimestamp = EventTime, EndTimestamp = EventTime.AddMinutes(30), Rate = 0.8, DataSource = Connector,
        }], WriteOrigin.Live);

        (await _tempBasals.GetAsync(EventTime.AddHours(-1), EventTime.AddHours(1), null, null))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task MigrationImport_AndConnectorHistory_ShowEachEventOnce()
    {
        await _boluses.BulkCreateAsync(
            [Bolus(1.5, EventTime, Connector), Bolus(0.4, EventTime.AddMinutes(5), Connector)], WriteOrigin.Backfill);
        await _carbs.BulkCreateAsync([Carb(30, EventTime, Connector)], WriteOrigin.Backfill);
        await _glucose.BulkCreateAsync(
            [new SensorGlucose { Timestamp = EventTime, Mgdl = 118, DataSource = Connector }], WriteOrigin.Backfill);

        await _boluses.BulkCreateAsync(
            [Bolus(1.5, EventTime, null, "5f0c1a2b3c4d5e6f70819203"), Bolus(0.4, EventTime.AddMinutes(5), null, "5f0c1a2b3c4d5e6f70819204")],
            WriteOrigin.Backfill);
        await _carbs.BulkCreateAsync([Carb(30, EventTime, null, "5f0c1a2b3c4d5e6f70819205")], WriteOrigin.Backfill);
        await _glucose.BulkCreateAsync(
            [new SensorGlucose { Timestamp = EventTime, Mgdl = 118, LegacyId = "5f0c1a2b3c4d5e6f70819206" }],
            WriteOrigin.Backfill);

        (await VisibleBolusesAsync()).Should().HaveCount(2);
        (await VisibleCarbsAsync()).Should().ContainSingle();
        (await _glucose.GetAsync(EventTime.AddHours(-1), EventTime.AddHours(1), null, null)).Should().ContainSingle();
    }

    [Fact]
    public async Task TwoManualDosesOfTheSameAmount_MinutesApart_StayTwoDoses()
    {
        await _boluses.CreateAsync(Bolus(2, EventTime, null, "syn-manual-1"), WriteOrigin.Live);
        await _boluses.CreateAsync(Bolus(2, EventTime.AddMinutes(4), null, "syn-manual-2"), WriteOrigin.Live);
        await _boluses.BulkCreateAsync([Bolus(2, EventTime.AddMinutes(4), Connector)], WriteOrigin.Live);

        var visible = (await VisibleBolusesAsync()).ToList();

        visible.Should().HaveCount(2);
        visible.Select(b => b.Timestamp).Should().BeEquivalentTo([EventTime, EventTime.AddMinutes(4)]);
    }

    [Fact]
    public async Task SourcelessDose_MinutesFromAConnectorDoseOfTheSameAmount_IsNotMerged()
    {
        await _boluses.BulkCreateAsync([Bolus(3, EventTime, Connector)], WriteOrigin.Live);
        await _boluses.CreateAsync(Bolus(3, EventTime.AddMinutes(3), null, "syn-manual"), WriteOrigin.Live);

        (await VisibleBolusesAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task DifferentAmountsAtTheSameMoment_StayTwoDoses()
    {
        await _carbs.CreateAsync(Carb(12, EventTime, null, "syn-a"), WriteOrigin.Live);
        await _carbs.CreateAsync(Carb(40, EventTime, null, "syn-b"), WriteOrigin.Live);

        (await VisibleCarbsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task NativeRecordWrittenFirst_StaysTheVisibleCopy()
    {
        var native = await _carbs.CreateAsync(Carb(18, EventTime, Manual), WriteOrigin.Live);
        await _carbs.BulkCreateAsync([Carb(18, EventTime, Connector)], WriteOrigin.Live);

        var nativeOnly = await _carbs.GetAsync(
            EventTime.AddHours(-1), EventTime.AddHours(1), null, null, nativeOnly: true);

        nativeOnly.Should().ContainSingle().Which.Id.Should().Be(native.Id);
        (await VisibleCarbsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task SingleCreate_LinksTheInsertedRow()
    {
        var created = await _carbs.CreateAsync(Carb(9, EventTime, null, "syn-linked"), WriteOrigin.Live);

        var link = await _context.LinkedRecords.AsNoTracking().SingleAsync(lr => lr.RecordId == created.Id);
        link.IsPrimary.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Manual)]
    public async Task SameAmountFromOneUploaderOrByHand_SecondsApart_StaysTwoDoses(string? source)
    {
        await _boluses.CreateAsync(Bolus(0.5, EventTime, source, "syn-dose-1"), WriteOrigin.Live);
        await _boluses.CreateAsync(Bolus(0.5, EventTime.AddSeconds(20), source, "syn-dose-2"), WriteOrigin.Live);
        await _carbs.CreateAsync(Carb(15, EventTime, source, "syn-carb-1"), WriteOrigin.Live);
        await _carbs.CreateAsync(Carb(15, EventTime.AddSeconds(20), source, "syn-carb-2"), WriteOrigin.Live);

        (await VisibleBolusesAsync()).Should().HaveCount(2);
        (await VisibleCarbsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task SameAmountFromOneSource_InOneBatch_StaysTwoDoses()
    {
        await _boluses.BulkCreateAsync(
            [Bolus(0.5, EventTime, null, "syn-b-1"), Bolus(0.5, EventTime.AddSeconds(20), null, "syn-b-2")],
            WriteOrigin.Live);

        (await VisibleBolusesAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task SameSizeDosesRelayedByTheNightscoutConnector_SecondsApart_StayTwoDoses()
    {
        await _boluses.CreateAsync(
            Bolus(0.5, EventTime, "nightscout-connector", "5f0c1a2b3c4d5e6f70819301"), WriteOrigin.Live);
        await _boluses.CreateAsync(
            Bolus(0.5, EventTime.AddSeconds(20), "nightscout-connector", "5f0c1a2b3c4d5e6f70819302"), WriteOrigin.Live);

        (await VisibleBolusesAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task TidepoolTwins_StillMerge()
    {
        await _carbs.BulkCreateAsync([Carb(30, EventTime, Connector)], WriteOrigin.Live);
        await _carbs.BulkCreateAsync([Carb(30, EventTime, Connector)], WriteOrigin.Live);

        (await VisibleCarbsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task TwoTidepoolBolusesOfOneSizeAtOneSecond_StayTwoDoses()
    {
        await _boluses.BulkCreateAsync(
            [Bolus(1.2, EventTime, Connector, "tidepool_b1"), Bolus(1.2, EventTime, Connector, "tidepool_b2")],
            WriteOrigin.Live);

        (await VisibleBolusesAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task SecondDoseFromTheUploader_BesideAMergedCopy_StaysVisible()
    {
        await _boluses.CreateAsync(Bolus(1, EventTime, null, "syn-first"), WriteOrigin.Live);
        await _boluses.BulkCreateAsync([Bolus(1, EventTime.AddSeconds(10), Connector)], WriteOrigin.Live);
        await _boluses.CreateAsync(Bolus(1, EventTime.AddSeconds(20), null, "syn-second"), WriteOrigin.Live);

        (await VisibleBolusesAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task NotesWithDifferentText_SecondsApart_StayTwoNotes()
    {
        await _notes.BulkCreateAsync(
        [
            new Note { Timestamp = EventTime, Text = "Walked to the shops", DataSource = Connector },
            new Note { Timestamp = EventTime.AddSeconds(10), Text = "Sensor changed", DataSource = "glooko-connector" },
        ], WriteOrigin.Live);

        (await _notes.GetAsync(EventTime.AddHours(-1), EventTime.AddHours(1), null, null)).Should().HaveCount(2);
    }

    [Fact]
    public async Task NotesWithTheSameTextReflowed_Merge()
    {
        await _notes.CreateAsync(new Note { Timestamp = EventTime, Text = "Pizza  for\ndinner" }, WriteOrigin.Live);
        await _notes.BulkCreateAsync(
            [new Note { Timestamp = EventTime.AddSeconds(5), Text = " Pizza for dinner ", DataSource = Connector }],
            WriteOrigin.Live);

        (await _notes.GetAsync(EventTime.AddHours(-1), EventTime.AddHours(1), null, null)).Should().ContainSingle();
    }

    [Fact]
    public async Task OpenEndedCancel_BesideATimedZeroTemp_StaysTwoTempBasals()
    {
        await _tempBasals.CreateAsync(new TempBasal
        {
            StartTimestamp = EventTime, Rate = 0, LegacyId = "syn-cancel",
        }, WriteOrigin.Live);
        await _tempBasals.BulkCreateAsync([new TempBasal
        {
            StartTimestamp = EventTime.AddSeconds(10), EndTimestamp = EventTime.AddMinutes(30), Rate = 0, DataSource = Connector,
        }], WriteOrigin.Live);

        (await _tempBasals.GetAsync(EventTime.AddHours(-1), EventTime.AddHours(1), null, null)).Should().HaveCount(2);
    }
}
