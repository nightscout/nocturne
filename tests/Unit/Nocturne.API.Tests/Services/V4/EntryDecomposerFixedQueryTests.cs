using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// An entry batch costs the same calls on its stores whatever its size: the repositories and the
/// device stamper are doubles that answer the set-based forms, and the calls a batch of one and a
/// batch of fifty make are counted.
/// </summary>
/// <remarks>
/// A mock-level count cannot see a store's own statements; the reads and the bulk upsert are capped per
/// call against PostgreSQL in <c>EntryAndProfileBatchReadsTests</c>. One call here is not one
/// statement: the read of the rows served-shaped ids name is one statement per 50 such ids per table,
/// so a page of write-back echoes pulled back scales with the page. The glucose-processing resolver is
/// asked once per reading but is no store: the settings it reads are memoised per scope
/// (<see cref="GlucoseProcessingConfigProvider"/>), so it is not counted. An edit of a reading served
/// under its own id is written onto that row (<c>DecomposerBase.WriteOntoStoredAsync</c>),
/// one call per edit; an edit is always a batch of one.
/// </remarks>
public class EntryDecomposerFixedQueryTests
{
    private const long Start = 1_780_000_000_000;

    private readonly List<Mock> _doubles = [];

    public static TheoryData<string> Kinds() => ["sgv", "mbg", "cal", "mixed", "echo"];

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ABatchOfFiftyCostsWhatABatchOfOneCosts(string kind)
    {
        var one = await CallsAsync(Batch(kind, kind == "mixed" ? 3 : 1));
        var fifty = await CallsAsync(Batch(kind, 50));

        fifty.Should().Be(one, $"a batch of {kind} entries reads and writes each store a fixed number of times");
        one.Should().BeGreaterThan(0);
    }

    private static List<Entry> Batch(string kind, int size) =>
        [.. Enumerable.Range(0, size).Select(i => Entry(kind switch
        {
            "mixed" => new[] { "sgv", "mbg", "cal" }[i % 3],
            "echo" => "sgv",
            _ => kind,
        }, i, served: kind == "echo"))];

    private static Entry Entry(string type, int i, bool served) => new()
    {
        Id = served ? $"5e00{i:x4}0000700080000000" : $"5e00{i:x4}0000000000000000",
        Type = type,
        Mills = Start + i * 300_000L,
        Sgv = type == "sgv" ? 100 + i : null,
        Mbg = type == "mbg" ? 100 + i : null,
        Slope = type == "cal" ? 900 : null,
        Device = "xDrip-DexcomG6",
    };

    private async Task<int> CallsAsync(IReadOnlyList<Entry> entries)
    {
        _doubles.Clear();
        await Decomposer().DecomposeBatchAsync(entries, WriteOrigin.Live);
        return _doubles.Sum(d => d.Invocations.Count);
    }

    private Mock<TRepository> Table<TRepository, TRecord>()
        where TRepository : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record
    {
        var mock = new Mock<TRepository>();
        _doubles.Add(mock);
        mock.As<ILegacyKeyedRepository<TRecord>>()
            .Setup(r => r.GetCorrelationIdsByLegacyIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mock.As<ILegacyKeyedRepository<TRecord>>()
            .Setup(r => r.GetByServedIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mock.As<IBulkUpsertRepository<TRecord>>()
            .Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<TRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<TRecord> records, WriteOrigin _, CancellationToken _) => new BulkWrite<TRecord>([.. records], 0));
        return mock;
    }

    private EntryDecomposer Decomposer()
    {
        var stamper = new Mock<IPatientDeviceStamper>();
        _doubles.Add(stamper);
        var config = new Mock<IGlucoseProcessingConfigProvider>();
        config.Setup(c => c.GetSourceDefaultsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new EntryDecomposer(
            Table<ISensorGlucoseRepository, SensorGlucose>().Object,
            Table<IMeterGlucoseRepository, MeterGlucose>().Object,
            Table<ICalibrationRepository, Calibration>().Object,
            new GlucoseProcessingResolver(config.Object),
            stamper.Object,
            Mock.Of<IAuditContext>(),
            NullLogger<EntryDecomposer>.Instance);
    }
}
