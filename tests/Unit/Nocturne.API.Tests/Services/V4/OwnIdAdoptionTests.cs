using Moq;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// Nightscout write-back sends a record upstream under a 24-hex id: its legacy id's prefix when that
/// legacy id is a uuid, its own uuid's prefix when it has no legacy id (older write-backs sent the
/// raw uuid). Before their legacy-id upserts run, both decomposers rewrite a pulled-back prefix to
/// the uuid-shaped legacy id it stands for, and otherwise let a record with no legacy id take the id
/// it is named by, so the copy coming back matches the record instead of landing beside it. The
/// repository side is covered against Postgres by <c>OwnIdAdoptionGoldenTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class OwnIdAdoptionTests : IDisposable
{
    private const string Uuid = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";
    private const string UuidPrefix = "0198c2a41f3b7c2d9e556a1b";

    private readonly NocturneDbContext _context = TestDbContextFactory.CreateInMemoryContext();
    private readonly List<string> _calls = [];

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <param name="unkeyed">The ids that name a record of the table with no legacy id.</param>
    private Mock<TRepo> LegacyKeyed<TRepo, TRecord>(string name, params string[] unkeyed)
        where TRepo : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record, new()
    {
        var repo = new Mock<TRepo>();
        repo.Setup(r => r.FindUnkeyedOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<string> ids, string _, CancellationToken _) =>
                _calls.Add($"{name}.find({string.Join(",", ids.Order())})"))
            .ReturnsAsync((IReadOnlyCollection<string> ids, string _, CancellationToken _) =>
                ids.Where(unkeyed.Contains).Select(id => new UnkeyedOwnId(id, WriteBackMaySend: false)).ToList());
        repo.Setup(r => r.AdoptOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<string> ids, CancellationToken _) =>
                _calls.Add($"{name}.adopt({string.Join(",", ids.Order())})"))
            .ReturnsAsync((IReadOnlyCollection<string> ids, CancellationToken _) =>
                ids.Where(unkeyed.Contains).Select(id => new TRecord { LegacyId = id }).ToList());
        repo.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<string> ids, CancellationToken _) =>
                _calls.Add($"{name}.held({string.Join(",", ids.Order())})"))
            .ReturnsAsync(new HashSet<string>());
        repo.Setup(r => r.ResolveUuidLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<string> ids, CancellationToken _) =>
                _calls.Add($"{name}.resolve({string.Join(",", ids.Order())})"))
            .ReturnsAsync([]);
        repo.Setup(r => r.AdoptLegacyIdsByCorrelationAsync(It.IsAny<IReadOnlyDictionary<Guid, string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyDictionary<Guid, string> map, CancellationToken _) =>
                _calls.Add($"{name}.adoptSiblings({string.Join(",", map.Select(kv => $"{kv.Key}={kv.Value}"))})"))
            .ReturnsAsync(0);
        repo.Setup(r => r.GetByLegacyIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string id, CancellationToken _) => _calls.Add($"{name}.get({id})"))
            .ReturnsAsync((TRecord?)null);
        repo.Setup(r => r.CreateAsync(It.IsAny<TRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TRecord record, WriteOrigin _, CancellationToken _) => record);
        repo.Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<TRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add($"{name}.upsert"))
            .ReturnsAsync((IEnumerable<TRecord> records, WriteOrigin _, CancellationToken _) => [.. records]);
        repo.Setup(r => r.GetByServedIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repo.Setup(r => r.GetCorrelationIdsByLegacyIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<string> ids, CancellationToken _) => _calls.Add($"{name}.correlations({string.Join(",", ids.Order())})"))
            .ReturnsAsync([]);
        return repo;
    }

    private IEnumerable<string> Lookups => _calls.Where(c => !c.Contains(".correlations("));

    private static Task DecomposeEntriesAsync(
        Mock<ISensorGlucoseRepository> sg, Mock<IMeterGlucoseRepository> mg, Mock<ICalibrationRepository> cal,
        IReadOnlyList<Entry> entries)
    {
        var config = new Mock<IGlucoseProcessingConfigProvider>();
        config.Setup(c => c.GetSourceDefaultsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return EntryDecomposerDoubles
            .Build(sg.Object, mg.Object, cal.Object, glucoseResolver: new GlucoseProcessingResolver(config.Object))
            .DecomposeBatchAsync(entries, WriteOrigin.Live);
    }

    [Fact]
    public async Task EntryBatch_OffersEachTableTheOwnIdsOfItsTypeBeforeUpserting()
    {
        var sg = LegacyKeyed<ISensorGlucoseRepository, SensorGlucose>("sg", Uuid);
        var mg = LegacyKeyed<IMeterGlucoseRepository, MeterGlucose>("mg");
        var cal = LegacyKeyed<ICalibrationRepository, Calibration>("cal");

        await DecomposeEntriesAsync(sg, mg, cal,
        [
            new Entry { Id = Uuid, Type = "sgv", Mills = 1_700_000_000_000, Sgv = 120, DataSource = DataSources.NightscoutConnector },
            new Entry { Id = "dexcom_7f3c2a91", Type = "sgv", Mills = 1_700_000_300_000, Sgv = 125 },
            new Entry { Id = UuidPrefix, Type = "mbg", Mills = 1_700_000_600_000, Mbg = 140 },
            new Entry { Id = "cal-1", Type = "cal", Mills = 1_700_000_900_000, Slope = 850 },
        ]);

        Lookups.Should().Equal(
            $"sg.held({Uuid})", $"sg.find({Uuid})", $"sg.adopt({Uuid})",
            $"mg.held({UuidPrefix})", $"mg.resolve({UuidPrefix})", $"mg.find({UuidPrefix})",
            "sg.upsert", "mg.upsert", "cal.upsert");
    }

    [Fact]
    public async Task EntryBatch_TouchesNoTableWhenNoIdCanNameAStoredRecord()
    {
        var sg = LegacyKeyed<ISensorGlucoseRepository, SensorGlucose>("sg");
        var mg = LegacyKeyed<IMeterGlucoseRepository, MeterGlucose>("mg");
        var cal = LegacyKeyed<ICalibrationRepository, Calibration>("cal");

        await DecomposeEntriesAsync(sg, mg, cal,
        [
            new Entry { Id = "507f1f77bcf80cd799439011", Type = "sgv", Mills = 1_700_000_000_000, Sgv = 120 },
            new Entry { Id = Uuid.ToUpperInvariant(), Type = "sgv", Mills = 1_700_000_300_000, Sgv = 125 },
            new Entry { Id = Uuid, Type = "sgv", Mills = 1_700_000_450_000, Sgv = 128 },
            new Entry { Id = null, Type = "sgv", Mills = 1_700_000_600_000, Sgv = 130 },
        ]);

        Lookups.Should().Equal("sg.upsert");
    }

    private (Mock<IApsSnapshotRepository> Aps, Mock<IPumpSnapshotRepository> Pump, Mock<IUploaderSnapshotRepository> Uploader,
        Func<IReadOnlyList<DeviceStatus>, string?, Task<DecompositionResult>> Decompose) DeviceStatuses(string[]? unkeyed = null)
    {
        var aps = LegacyKeyed<IApsSnapshotRepository, ApsSnapshot>("aps", unkeyed ?? [Uuid]);
        var pump = LegacyKeyed<IPumpSnapshotRepository, PumpSnapshot>("pump");
        pump.Setup(r => r.GetTimelineAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var uploader = LegacyKeyed<IUploaderSnapshotRepository, UploaderSnapshot>("uploader");
        var extras = new Mock<IDeviceStatusExtrasRepository>();
        extras.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<DeviceStatusExtras>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<DeviceStatusExtras> records, WriteOrigin _, CancellationToken _) => [.. records]);

        var spans = new Mock<IStateSpanService>();
        spans.BacksWith([]);
        var decomposer = DeviceStatusDecomposerDoubles.Build(
            aps.Object, pump.Object, uploader.Object, extras.Object, stateSpans: spans.Object);
        return (aps, pump, uploader, async (statuses, source) =>
            await decomposer.DecomposeBatchAsync(statuses, source, WriteOrigin.Live));
    }

    private static DeviceStatus Status(string id) => new()
    {
        Id = id,
        Device = "openaps://rig",
        Mills = 1_700_000_000_000,
        OpenAps = new OpenApsStatus(),
        Pump = new PumpStatus { Clock = "2023-11-14T22:13:20Z" },
    };

    [Fact]
    public async Task DeviceStatusBatch_GivesTheAnchorsSiblingsItsIdBeforeReadingStoredGroups()
    {
        var (aps, _, _, decompose) = DeviceStatuses();
        var correlationId = Guid.CreateVersion7();
        aps.Setup(r => r.AdoptOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<string> ids, CancellationToken _) => _calls.Add($"aps.adopt({string.Join(",", ids)})"))
            .ReturnsAsync([new ApsSnapshot { LegacyId = Uuid, CorrelationId = correlationId }]);

        await decompose([Status(Uuid), Status("loop_status_42")], null);

        _calls.Take(11).Should().Equal(
            $"aps.held({Uuid})", $"pump.held({Uuid})", $"uploader.held({Uuid})", $"aps.find({Uuid})",
            $"aps.adopt({Uuid})", $"pump.adopt({Uuid})", $"uploader.adopt({Uuid})",
            $"aps.adoptSiblings({correlationId}={Uuid})",
            $"pump.adoptSiblings({correlationId}={Uuid})",
            $"uploader.adoptSiblings({correlationId}={Uuid})",
            $"aps.correlations({Uuid},loop_status_42)");
    }

    [Fact]
    public async Task DeviceStatus_AdoptsNothingWhenNoStoredStatusCarriesTheId()
    {
        var (_, _, _, decompose) = DeviceStatuses(unkeyed: []);

        await decompose([Status(UuidPrefix)], null);

        _calls.Take(10).Should().Equal(
            $"aps.held({UuidPrefix})", $"pump.held({UuidPrefix})", $"uploader.held({UuidPrefix})",
            $"aps.resolve({UuidPrefix})", $"pump.resolve({UuidPrefix})", $"uploader.resolve({UuidPrefix})",
            $"aps.find({UuidPrefix})", $"pump.find({UuidPrefix})", $"uploader.find({UuidPrefix})",
            $"aps.correlations({UuidPrefix})");
    }

    [Fact]
    public async Task DeviceStatus_TouchesNoTableWhenTheIdCannotNameAStoredRecord()
    {
        var (_, _, _, decompose) = DeviceStatuses();

        await decompose([Status("loop_status_42")], null);

        _calls.Should().NotContain(c => c.Contains("adopt") || c.Contains("resolve"));
    }

    private const string UuidShapedLegacyId = "0198C2A4-1F3B-7C2D-9E55-6A1B2C3D4E5F";

    [Fact]
    public async Task EntryBatch_UpsertsAPrefixUnderTheUuidLegacyIdItStandsFor()
    {
        const string otherPrefix = "0198c2a41f3b7c2d9e55ffff";
        var sg = LegacyKeyed<ISensorGlucoseRepository, SensorGlucose>("sg", otherPrefix);
        var mg = LegacyKeyed<IMeterGlucoseRepository, MeterGlucose>("mg");
        var cal = LegacyKeyed<ICalibrationRepository, Calibration>("cal");
        sg.Setup(r => r.ResolveUuidLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new WireLegacyId(UuidPrefix, UuidShapedLegacyId)]);
        List<SensorGlucose>? written = null;
        sg.Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<SensorGlucose> records, WriteOrigin _, CancellationToken _) => written = [.. records])
            .ReturnsAsync((IEnumerable<SensorGlucose> records, WriteOrigin _, CancellationToken _) => [.. records]);
        await DecomposeEntriesAsync(sg, mg, cal,
        [
            new Entry { Id = UuidPrefix, Type = "sgv", Mills = 1_700_000_000_000, Sgv = 120 },
            new Entry { Id = otherPrefix, Type = "sgv", Mills = 1_700_000_300_000, Sgv = 125 },
        ]);

        written!.Select(r => r.LegacyId).Should().Equal(UuidShapedLegacyId, otherPrefix);
        sg.Verify(r => r.AdoptOwnIdsAsync(
            It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { otherPrefix })), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task DeviceStatus_DecomposesAPrefixUnderTheUuidLegacyIdItStandsFor()
    {
        var (_, pump, _, decompose) = DeviceStatuses();
        pump.Setup(r => r.ResolveUuidLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new WireLegacyId(UuidPrefix, UuidShapedLegacyId)]);
        var status = Status(UuidPrefix);

        await decompose([status], null);

        status.Id.Should().Be(UuidShapedLegacyId);
        _calls.Should().NotContain(c => c.Contains(".adopt("));
        _calls.Should().Contain($"pump.correlations({UuidShapedLegacyId})");
    }

    /// <summary>
    /// Nightscout 15.0.7 and later give a written-back copy a <c>_id</c> of their own, so the
    /// record is recognised by the <c>identifier</c> write-back sent: a legacy id a stored row holds,
    /// or a keyless record's own uuid. Any other identifier belongs to another client, and the copy
    /// stays under its <c>_id</c>, which is how every earlier pull stored it.
    /// </summary>
    [Fact]
    public async Task EntryBatch_MatchesAPulledCopyByItsIdentifierBeforeItsId()
    {
        var sg = LegacyKeyed<ISensorGlucoseRepository, SensorGlucose>("sg", Uuid);
        var mg = LegacyKeyed<IMeterGlucoseRepository, MeterGlucose>("mg");
        var cal = LegacyKeyed<ICalibrationRepository, Calibration>("cal");
        sg.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "dexcom_7f3c2a91" });
        List<SensorGlucose>? written = null;
        sg.Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<SensorGlucose> records, WriteOrigin _, CancellationToken _) => written = [.. records])
            .ReturnsAsync((IEnumerable<SensorGlucose> records, WriteOrigin _, CancellationToken _) => [.. records]);

        await DecomposeEntriesAsync(sg, mg, cal,
        [
            Pulled(new Entry { Id = "66f0a1b2c3d4e0f6a7b8c9d0", UpstreamIdentifier = "dexcom_7f3c2a91", Type = "sgv", Mills = 1_700_000_000_000, Sgv = 120 }),
            Pulled(new Entry { Id = "66f0a1b2c3d4e0f6a7b8c9d1", UpstreamIdentifier = Uuid, Type = "sgv", Mills = 1_700_000_300_000, Sgv = 121 }),
            Pulled(new Entry { Id = "66f0a1b2c3d4e0f6a7b8c9d2", UpstreamIdentifier = "trio-7c2d", Type = "sgv", Mills = 1_700_000_600_000, Sgv = 122 }),
        ]);

        written!.Select(r => r.LegacyId).Should().Equal("dexcom_7f3c2a91", Uuid, "66f0a1b2c3d4e0f6a7b8c9d2");
    }

    [Fact]
    public async Task DeviceStatus_MatchesAPulledCopyByAnIdentifierTheUsersDeletionHolds()
    {
        var (_, _, uploader, decompose) = DeviceStatuses();
        uploader.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "loop_status_42" });
        var status = Status("66f0a1b2c3d4e0f6a7b8c9d0");
        status.UpstreamIdentifier = "loop_status_42";

        await decompose([status], DataSources.NightscoutConnector);

        status.Id.Should().Be("loop_status_42");
        _calls.Should().NotContain(c => c.Contains(".adopt(") || c.Contains(".resolve(") || c.Contains(".find("));
        _calls.Should().Contain("aps.correlations(loop_status_42)");
    }

    /// <summary>
    /// A copy the Nightscout connector pulls that names a record write-back may have sent upstream is that
    /// record's write-back echo. It keeps the identity it was pointed at and writes nothing, so the
    /// record keeps its attribution and any edit made in Nocturne since.
    /// </summary>
    [Fact]
    public async Task EntryBatch_WritesNothingForAnEchoOfAReadingAnotherSourceStores()
    {
        var sg = LegacyKeyed<ISensorGlucoseRepository, SensorGlucose>("sg");
        var mg = LegacyKeyed<IMeterGlucoseRepository, MeterGlucose>("mg");
        var cal = LegacyKeyed<ICalibrationRepository, Calibration>("cal");
        sg.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "dexcom_7f3c2a91" });
        sg.Setup(r => r.GetLegacyIdsWriteBackMaySendAsync(
                It.IsAny<IReadOnlyCollection<string>>(), DataSources.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new WriteBackSendable("dexcom_7f3c2a91", null, null)]);
        List<SensorGlucose>? written = null;
        sg.Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<SensorGlucose> records, WriteOrigin _, CancellationToken _) => written = [.. records])
            .ReturnsAsync((IEnumerable<SensorGlucose> records, WriteOrigin _, CancellationToken _) => [.. records]);
        var echo = new Entry
        {
            Id = "66f0a1b2c3d4e0f6a7b8c9d0", UpstreamIdentifier = "dexcom_7f3c2a91", Type = "sgv",
            Mills = 1_700_000_000_000, Sgv = 120, DataSource = DataSources.NightscoutConnector,
        };
        var pulled = new Entry
        {
            Id = "66f0a1b2c3d4e0f6a7b8c9d1", Type = "sgv", Mills = 1_700_000_300_000, Sgv = 121,
            DataSource = DataSources.NightscoutConnector,
        };

        await DecomposeEntriesAsync(sg, mg, cal, [echo, pulled]);

        echo.Id.Should().Be("dexcom_7f3c2a91");
        written!.Select(r => r.LegacyId).Should().Equal("66f0a1b2c3d4e0f6a7b8c9d1");
    }

    [Fact]
    public async Task EntryBatch_UpdatesARecordAnotherSourceStoresWhenTheCopyIsNotFromTheNightscoutConnector()
    {
        var sg = LegacyKeyed<ISensorGlucoseRepository, SensorGlucose>("sg");
        var mg = LegacyKeyed<IMeterGlucoseRepository, MeterGlucose>("mg");
        var cal = LegacyKeyed<ICalibrationRepository, Calibration>("cal");
        sg.Setup(r => r.GetLegacyIdsWriteBackMaySendAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new WriteBackSendable("dexcom_7f3c2a91", null, null)]);

        await DecomposeEntriesAsync(sg, mg, cal,
            [new Entry { Id = "dexcom_7f3c2a91", Type = "sgv", Mills = 1_700_000_000_000, Sgv = 120, DataSource = "xdrip" }]);

        Lookups.Should().Equal("sg.upsert");
        sg.Verify(r => r.GetLegacyIdsWriteBackMaySendAsync(
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeviceStatus_WritesNothingForAnEchoOfAStatusAnotherSourceStores()
    {
        var (aps, _, _, decompose) = DeviceStatuses();
        aps.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "loop_status_42" });
        aps.Setup(r => r.GetLegacyIdsWriteBackMaySendAsync(
                It.IsAny<IReadOnlyCollection<string>>(), DataSources.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new WriteBackSendable("loop_status_42", null, null)]);
        var status = Status("66f0a1b2c3d4e0f6a7b8c9d0");
        status.UpstreamIdentifier = "loop_status_42";

        var result = await decompose([status], DataSources.NightscoutConnector);

        status.Id.Should().Be("loop_status_42");
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _calls.Should().NotContain(c => c.Contains(".correlations("));
    }

    private static Entry Pulled(Entry entry)
    {
        entry.DataSource = DataSources.NightscoutConnector;
        return entry;
    }

    /// <summary>
    /// Only a record write-back may have sent upstream has a write-back echo. One only an import
    /// wrote, such as a Nightscout migration, is the upstream's own record, and the pull updates it:
    /// an upstream edit (a dose marked invalid, a temp basal cancelled early) must reach Nocturne.
    /// </summary>
    [Fact]
    public async Task EntryBatch_UpdatesAReadingWriteBackNeverSent()
    {
        var sg = LegacyKeyed<ISensorGlucoseRepository, SensorGlucose>("sg");
        var mg = LegacyKeyed<IMeterGlucoseRepository, MeterGlucose>("mg");
        var cal = LegacyKeyed<ICalibrationRepository, Calibration>("cal");
        sg.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "5f1a2b3c4d5e6f7a8b9c0d1e" });
        sg.Setup(r => r.GetLegacyIdsWriteBackMaySendAsync(
                It.IsAny<IReadOnlyCollection<string>>(), DataSources.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        List<SensorGlucose>? written = null;
        sg.Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<SensorGlucose> records, WriteOrigin _, CancellationToken _) => written = [.. records])
            .ReturnsAsync((IEnumerable<SensorGlucose> records, WriteOrigin _, CancellationToken _) => [.. records]);

        await DecomposeEntriesAsync(sg, mg, cal,
            [Pulled(new Entry { Id = "5f1a2b3c4d5e6f7a8b9c0d1e", Type = "sgv", Mills = 1_700_000_000_000, Sgv = 131 })]);

        written!.Select(r => (r.LegacyId, r.Mgdl)).Should().Equal(("5f1a2b3c4d5e6f7a8b9c0d1e", 131d));
    }

    [Fact]
    public async Task DeviceStatus_UpdatesAStatusWriteBackNeverSent()
    {
        var (aps, _, _, decompose) = DeviceStatuses();
        aps.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "loop_status_42" });
        aps.Setup(r => r.GetLegacyIdsWriteBackMaySendAsync(
                It.IsAny<IReadOnlyCollection<string>>(), DataSources.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var status = Status("66f0a1b2c3d4e0f6a7b8c9d0");
        status.UpstreamIdentifier = "loop_status_42";

        await decompose([status], DataSources.NightscoutConnector);

        status.Id.Should().Be("loop_status_42");
        _calls.Should().Contain("aps.correlations(loop_status_42)");
    }
}
