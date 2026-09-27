using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A stored record with no legacy id goes out on the wire, and to an upstream Nightscout through
/// write-back, under its own uuid or that uuid's 24-hex prefix. Both decomposers let such a record
/// take that id as its legacy id before their legacy-id upserts run, so the copy coming back matches
/// it instead of landing beside it. The repository side is covered against Postgres by
/// <c>OwnIdAdoptionIntegrationTests</c>.
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

    private Mock<TRepo> LegacyKeyed<TRepo, TRecord>(string name)
        where TRepo : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record
    {
        var repo = new Mock<TRepo>();
        repo.Setup(r => r.AdoptOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<string> ids, CancellationToken _) =>
                _calls.Add($"{name}.adopt({string.Join(",", ids.Order())})"))
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
        repo.Setup(r => r.GetCorrelationIdsByLegacyIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add($"{name}.correlations"))
            .ReturnsAsync([]);
        return repo;
    }

    private EntryDecomposer EntryDecomposer(
        Mock<ISensorGlucoseRepository> sg, Mock<IMeterGlucoseRepository> mg, Mock<ICalibrationRepository> cal)
    {
        var config = new Mock<IGlucoseProcessingConfigProvider>();
        config.Setup(c => c.GetSourceDefaultsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new EntryDecomposer(
            _context, sg.Object, mg.Object, cal.Object,
            new GlucoseProcessingResolver(config.Object),
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IAuditContext>(),
            NullLogger<EntryDecomposer>.Instance);
    }

    [Fact]
    public async Task EntryBatch_OffersEachTableTheOwnIdsOfItsTypeBeforeUpserting()
    {
        var sg = LegacyKeyed<ISensorGlucoseRepository, SensorGlucose>("sg");
        var mg = LegacyKeyed<IMeterGlucoseRepository, MeterGlucose>("mg");
        var cal = LegacyKeyed<ICalibrationRepository, Calibration>("cal");

        await EntryDecomposer(sg, mg, cal).DecomposeBatchAsync(
        [
            new Entry { Id = Uuid, Type = "sgv", Mills = 1_700_000_000_000, Sgv = 120 },
            new Entry { Id = "dexcom_7f3c2a91", Type = "sgv", Mills = 1_700_000_300_000, Sgv = 125 },
            new Entry { Id = UuidPrefix, Type = "mbg", Mills = 1_700_000_600_000, Mbg = 140 },
            new Entry { Id = "cal-1", Type = "cal", Mills = 1_700_000_900_000, Slope = 850 },
        ], WriteOrigin.Live);

        _calls.Should().Equal(
            $"sg.adopt({Uuid})", $"mg.adopt({UuidPrefix})",
            "sg.upsert", "mg.upsert", "cal.upsert");
    }

    [Fact]
    public async Task EntrySingle_OffersTheOwnIdBeforeLookingTheReadingUp()
    {
        var sg = LegacyKeyed<ISensorGlucoseRepository, SensorGlucose>("sg");
        var mg = LegacyKeyed<IMeterGlucoseRepository, MeterGlucose>("mg");
        var cal = LegacyKeyed<ICalibrationRepository, Calibration>("cal");

        await EntryDecomposer(sg, mg, cal).DecomposeAsync(
            new Entry { Id = UuidPrefix, Type = "cal", Mills = 1_700_000_000_000, Slope = 850 }, WriteOrigin.Live);

        _calls.Should().Equal($"cal.adopt({UuidPrefix})", $"cal.get({UuidPrefix})");
    }

    [Fact]
    public async Task EntryBatch_TouchesNoTableWhenNoIdCanNameAStoredRecord()
    {
        var sg = LegacyKeyed<ISensorGlucoseRepository, SensorGlucose>("sg");
        var mg = LegacyKeyed<IMeterGlucoseRepository, MeterGlucose>("mg");
        var cal = LegacyKeyed<ICalibrationRepository, Calibration>("cal");

        await EntryDecomposer(sg, mg, cal).DecomposeBatchAsync(
        [
            new Entry { Id = "507f1f77bcf80cd799439011", Type = "sgv", Mills = 1_700_000_000_000, Sgv = 120 },
            new Entry { Id = Uuid.ToUpperInvariant(), Type = "sgv", Mills = 1_700_000_300_000, Sgv = 125 },
            new Entry { Id = null, Type = "sgv", Mills = 1_700_000_600_000, Sgv = 130 },
        ], WriteOrigin.Live);

        _calls.Should().Equal("sg.upsert");
    }

    private (DeviceStatusDecomposer Decomposer, Mock<IApsSnapshotRepository> Aps, Mock<IPumpSnapshotRepository> Pump,
        Mock<IUploaderSnapshotRepository> Uploader) DeviceStatusDecomposer()
    {
        var aps = LegacyKeyed<IApsSnapshotRepository, ApsSnapshot>("aps");
        var pump = LegacyKeyed<IPumpSnapshotRepository, PumpSnapshot>("pump");
        var uploader = LegacyKeyed<IUploaderSnapshotRepository, UploaderSnapshot>("uploader");
        var extras = new Mock<IDeviceStatusExtrasRepository>();
        extras.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<DeviceStatusExtras>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<DeviceStatusExtras> records, WriteOrigin _, CancellationToken _) => [.. records]);

        var decomposer = new DeviceStatusDecomposer(
            aps.Object, pump.Object, uploader.Object, extras.Object,
            Mock.Of<IStateSpanService>(), Mock.Of<IDeviceService>(), Mock.Of<IAuditContext>(),
            NullLogger<DeviceStatusDecomposer>.Instance);
        return (decomposer, aps, pump, uploader);
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
        var (decomposer, aps, _, _) = DeviceStatusDecomposer();
        var correlationId = Guid.CreateVersion7();
        aps.Setup(r => r.AdoptOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<string> ids, CancellationToken _) => _calls.Add($"aps.adopt({string.Join(",", ids)})"))
            .ReturnsAsync([new ApsSnapshot { LegacyId = Uuid, CorrelationId = correlationId }]);

        await decomposer.DecomposeBatchAsync([Status(Uuid), Status("loop_status_42")], source: null, WriteOrigin.Live);

        _calls.Take(7).Should().Equal(
            $"aps.adopt({Uuid})", $"pump.adopt({Uuid})", $"uploader.adopt({Uuid})",
            $"aps.adoptSiblings({correlationId}={Uuid})",
            $"pump.adoptSiblings({correlationId}={Uuid})",
            $"uploader.adoptSiblings({correlationId}={Uuid})",
            "aps.correlations");
    }

    [Fact]
    public async Task DeviceStatusSingle_SkipsTheSiblingsWhenNoStoredStatusTookTheId()
    {
        var (decomposer, _, _, _) = DeviceStatusDecomposer();

        await decomposer.DecomposeAsync(Status(UuidPrefix), source: null, WriteOrigin.Live);

        _calls.Take(4).Should().Equal(
            $"aps.adopt({UuidPrefix})", $"pump.adopt({UuidPrefix})", $"uploader.adopt({UuidPrefix})",
            "aps.correlations");
    }

    [Fact]
    public async Task DeviceStatus_TouchesNoTableWhenTheIdCannotNameAStoredRecord()
    {
        var (decomposer, _, _, _) = DeviceStatusDecomposer();

        await decomposer.DecomposeAsync(Status("loop_status_42"), source: null, WriteOrigin.Live);

        _calls.Should().NotContain(c => c.Contains("adopt"));
    }
}
