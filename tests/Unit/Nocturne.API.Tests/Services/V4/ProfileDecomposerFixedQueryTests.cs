using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A profile batch costs the same calls on its stores whatever its size: the five tables and the two
/// followers are doubles, and the calls a batch of one document and of fifty make are counted.
/// </summary>
/// <remarks>
/// A mock-level count cannot see a store's own statements; the upserts and the sibling reads are
/// capped per call against PostgreSQL in <c>EntryAndProfileBatchReadsTests</c>. Two followers run a
/// statement per item they are handed rather than per batch: the Loop stamp, once per changed
/// settings start, since <c>StampLoopClosedLoopAsync</c> stamps only to the next settings version; and
/// the temp-basal re-resolution, once per profile name it refreshes.
/// </remarks>
public class ProfileDecomposerFixedQueryTests
{
    private readonly List<Mock> _doubles = [];

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ABatchOfFiftyDocumentsCostsWhatABatchOfOneCosts(int stores)
    {
        var one = await CallsAsync(Batch(1, stores));
        var fifty = await CallsAsync(Batch(50, stores));

        fifty.Should().Be(one, "a batch of profile documents reads and writes each store a fixed number of times");
        one.Should().BeGreaterThan(0);
    }

    private static List<Profile> Batch(int size, int stores) =>
    [
        .. Enumerable.Range(0, size).Select(i => new Profile
        {
            Id = $"5f00{i:x4}0000000000000000",
            StartDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i).ToString("O"),
            DefaultProfile = "Store0",
            Store = Enumerable.Range(0, stores).ToDictionary(s => $"Store{s}", _ => new ProfileData
            {
                Basal = [new TimeValue { Time = "00:00", Value = 0.8 }],
                CarbRatio = [new TimeValue { Time = "00:00", Value = 10 }],
                Sens = [new TimeValue { Time = "00:00", Value = 50 }],
                TargetLow = [new TimeValue { Time = "00:00", Value = 90 }],
                TargetHigh = [new TimeValue { Time = "00:00", Value = 120 }],
            }),
        }),
    ];

    private async Task<int> CallsAsync(IReadOnlyList<Profile> profiles)
    {
        _doubles.Clear();
        await Decomposer().DecomposeBatchAsync(profiles, WriteOrigin.Live);
        return _doubles.Sum(d => d.Invocations.Count);
    }

    private Mock<T> Double<T>() where T : class
    {
        var mock = new Mock<T>();
        _doubles.Add(mock);
        return mock;
    }

    private Mock<TRepository> Table<TRepository, TRecord>()
        where TRepository : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record
    {
        var mock = Double<TRepository>();
        mock.As<ILegacyKeyedRepository<TRecord>>()
            .Setup(r => r.BulkUpsertByLegacyIdAsync(
                It.IsAny<IReadOnlyList<TRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TRecord> records, WriteOrigin _, bool _, CancellationToken _) =>
                new LegacyUpsertBatch<TRecord>(
                    records.ToDictionary(r => r.LegacyId!, r => new LegacyUpsert<TRecord>(r, UpsertOutcome.Created)),
                    0));
        return mock;
    }

    private ProfileDecomposer Decomposer()
    {
        var therapy = Table<ITherapySettingsRepository, TherapySettings>();
        therapy.Setup(r => r.GetDefaultsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new ProfileDecomposer(
            therapy.Object,
            Table<IBasalScheduleRepository, BasalSchedule>().Object,
            Table<ICarbRatioScheduleRepository, CarbRatioSchedule>().Object,
            Table<ISensitivityScheduleRepository, SensitivitySchedule>().Object,
            Table<ITargetRangeScheduleRepository, TargetRangeSchedule>().Object,
            Double<ITempBasalRateReresolver>().Object,
            Double<ILoopClosedLoopStamper>().Object,
            NullLogger<ProfileDecomposer>.Instance);
    }
}
