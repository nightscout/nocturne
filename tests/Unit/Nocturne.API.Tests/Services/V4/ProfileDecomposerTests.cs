using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

public class ProfileDecomposerTests
{
    /// <summary>
    /// Profiles persist ONLY as the five decomposed granular records, so on the HTTP path
    /// (v1/v3 profile create/update) their audit rows are the entire mutation trail for a
    /// user's profile edit. Decomposition must NOT push a SystemAuditScope — connector
    /// re-syncs are suppressed by the sync scope's system audit context instead, and
    /// byte-identical re-upserts diff to empty and are skipped.
    /// </summary>
    [Fact]
    public async Task DecomposeAsync_PreservesCallerAuditAttribution()
    {
        var auditContext = new AuditContext { AuthType = "ApiKey", SubjectName = "someone" };
        var attributionDuringUpsert = new List<(bool IsSystem, string? AuthType)>();
        var repos = new Repositories(onUpsert: () =>
            attributionDuringUpsert.Add((auditContext.IsSystem, auditContext.AuthType)));

        var result = await repos.Decomposer.DecomposeAsync(BuildProfile(), WriteOrigin.Live);

        result.CreatedRecords.Should().HaveCount(5);
        attributionDuringUpsert.Should().HaveCount(5).And.AllSatisfy(a =>
        {
            a.IsSystem.Should().BeFalse("a user's profile edit must stay user-attributed in the audit log");
            a.AuthType.Should().Be("ApiKey");
        });
    }

    /// <summary>
    /// Only the therapy settings row may keep its stored correlation id; the four schedules are
    /// stamped from what it reads back, so preserving on them would let a fork survive.
    /// </summary>
    [Fact]
    public async Task DecomposeAsync_PreservesTheStoredCorrelationIdOnTheAnchorOnly()
    {
        var repos = new Repositories();

        await repos.Decomposer.DecomposeAsync(BuildProfile(), WriteOrigin.Live);

        repos.PreserveFlags.Should().HaveCount(5);
        repos.PreserveFlags[typeof(TherapySettings)].Should().BeTrue();
        repos.PreserveFlags.Where(p => p.Key != typeof(TherapySettings))
            .Should().AllSatisfy(p => p.Value.Should().BeFalse());
    }

    /// <summary>
    /// The five siblings are written in five separate saves, so one lost to a cancelled sync is
    /// recreated on the next one. It must rejoin the group rather than fork it: ProfileProjectionService
    /// loads the schedules by the therapy settings row's correlation id, and on a miss serves an empty
    /// schedule rather than failing.
    /// </summary>
    [Fact]
    public async Task DecomposeAsync_StampsTheSchedulesWithTheAnchorsStoredCorrelationId()
    {
        var anchor = Guid.CreateVersion7();
        var repos = new Repositories(anchorCorrelationId: anchor);

        await repos.Decomposer.DecomposeAsync(BuildProfile(), WriteOrigin.Live);

        repos.Written.Where(r => r is not TherapySettings).Should().HaveCount(4).And.AllSatisfy(
            r => r.CorrelationId.Should().Be(anchor, "a sibling must land on the therapy settings row's id"));
    }

    /// <summary>
    /// A stored id of null must not leave the anchor on a fresh id while the siblings keep theirs.
    /// </summary>
    [Fact]
    public async Task DecomposeAsync_StampsTheWholeGroupWithTheMintedId_WhenTheAnchorStoresNone()
    {
        var repos = new Repositories(anchorCorrelationId: null);

        var result = await repos.Decomposer.DecomposeAsync(BuildProfile(), WriteOrigin.Live);

        var ids = repos.Written.Select(r => r.CorrelationId).ToList();
        ids.Should().HaveCount(5);
        ids.Distinct().Should().ContainSingle().Which.Should().Be(result.CorrelationId!.Value,
            "the group must not fork when the anchor stored no id");
    }

    /// <summary>
    /// A refused anchor leaves the schedules nothing to converge on; writing them under the minted id
    /// would fork the group off the settings row they belong to.
    /// </summary>
    [Fact]
    public async Task DecomposeAsync_WritesNoSchedules_ForAStoreWhoseAnchorWasRefused()
    {
        var repos = new Repositories(refusedLegacyIds: ["profile1:Weekend"]);

        var result = await repos.Decomposer.DecomposeAsync(BuildProfile(stores: ["Default", "Weekend"]), WriteOrigin.Live);

        repos.Written.Select(r => r.LegacyId).Should().OnlyContain(id => id == "profile1:Default");
        repos.Written.Should().HaveCount(5);
        result.CreatedRecords.Should().HaveCount(5);
    }

    /// <summary>
    /// The point of the batch: a connector's whole profile set costs one round per table, not ten
    /// per named profile, and every store of every profile is in it.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_CallsEachRepositoryOnce_WithEveryStoreEntry()
    {
        var repos = new Repositories();
        var profiles = new[]
        {
            BuildProfile(id: "profile1", stores: ["Default", "Weekend"]),
            BuildProfile(id: "profile2", stores: ["Default"]),
        };

        var result = await repos.Decomposer.DecomposeBatchAsync(profiles, WriteOrigin.Live);

        repos.Calls.Should().HaveCount(5);
        repos.Calls.Should().AllSatisfy(c => c.Should().Be(3));
        repos.Written.Select(r => r.LegacyId).Distinct()
            .Should().BeEquivalentTo(["profile1:Default", "profile1:Weekend", "profile2:Default"]);
        result.CreatedRecords.Should().HaveCount(15);
    }

    /// <summary>
    /// Two profiles created together must not share a correlation id, while the stores of one profile
    /// do — the ids the single-profile path always minted.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_MintsOneCorrelationIdPerProfile()
    {
        var repos = new Repositories(anchorCorrelationId: null);
        var profiles = new[]
        {
            BuildProfile(id: "profile1", stores: ["Default", "Weekend"]),
            BuildProfile(id: "profile2", stores: ["Default"]),
        };

        var result = await repos.Decomposer.DecomposeBatchAsync(profiles, WriteOrigin.Live);

        var byProfile = repos.Written
            .GroupBy(r => r.LegacyId!.Split(':')[0])
            .ToDictionary(g => g.Key, g => g.Select(r => r.CorrelationId).Distinct().ToList());
        byProfile["profile1"].Should().ContainSingle().Which.Should().Be(result.CorrelationId!.Value);
        byProfile["profile2"].Should().ContainSingle().Which.Should().NotBe(result.CorrelationId!.Value);
    }

    [Fact]
    public async Task DecomposeBatchAsync_WithNoStoreEntries_WritesNothing()
    {
        var repos = new Repositories();

        var result = await repos.Decomposer.DecomposeBatchAsync(
            [BuildProfile(stores: [])], WriteOrigin.Live);

        repos.Calls.Should().BeEmpty();
        result.CreatedRecords.Should().BeEmpty();
    }

    /// <summary>
    /// Five mocked repositories whose bulk upsert answers every record as created, under the
    /// correlation id it was handed — except the therapy settings anchor, which answers with
    /// <paramref name="anchorCorrelationId"/> as its stored id when one is given, and drops any
    /// record whose legacy id is in <paramref name="refusedLegacyIds"/>.
    /// </summary>
    private sealed class Repositories
    {
        public ProfileDecomposer Decomposer { get; }
        public List<IV4Record> Written { get; } = [];
        public Dictionary<Type, bool> PreserveFlags { get; } = [];
        public List<int> Calls { get; } = [];

        public Repositories(
            Guid? anchorCorrelationId = null,
            IReadOnlyCollection<string>? refusedLegacyIds = null,
            Action? onUpsert = null)
        {
            var refused = refusedLegacyIds ?? [];

            Decomposer = new ProfileDecomposer(
                Mock<ITherapySettingsRepository, TherapySettings>(anchorCorrelationId, refused, onUpsert),
                Mock<IBasalScheduleRepository, BasalSchedule>(null, [], onUpsert),
                Mock<ICarbRatioScheduleRepository, CarbRatioSchedule>(null, [], onUpsert),
                Mock<ISensitivityScheduleRepository, SensitivitySchedule>(null, [], onUpsert),
                Mock<ITargetRangeScheduleRepository, TargetRangeSchedule>(null, [], onUpsert),
                NullLogger<ProfileDecomposer>.Instance);
        }

        private TRepo Mock<TRepo, TRecord>(
            Guid? storedCorrelationId, IReadOnlyCollection<string> refused, Action? onUpsert)
            where TRepo : class, ILegacyKeyedRepository<TRecord>
            where TRecord : class, IV4Record
        {
            var repo = new Moq.Mock<TRepo>();
            repo
                .Setup(x => x.BulkUpsertByLegacyIdAsync(
                    It.IsAny<IReadOnlyList<TRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<TRecord> records, WriteOrigin _, bool preserve, CancellationToken _) =>
                {
                    onUpsert?.Invoke();
                    Calls.Add(records.Count);
                    PreserveFlags[typeof(TRecord)] = preserve;
                    var outcomes = new Dictionary<string, LegacyUpsert<TRecord>>(StringComparer.Ordinal);
                    foreach (var record in records)
                    {
                        if (refused.Contains(record.LegacyId!))
                            continue;
                        if (storedCorrelationId is { } stored)
                            record.CorrelationId = stored;
                        Written.Add(record);
                        outcomes[record.LegacyId!] = new LegacyUpsert<TRecord>(record, Created: true);
                    }
                    return outcomes;
                });
            return repo.Object;
        }
    }

    private static Profile BuildProfile(string id = "profile1", IReadOnlyList<string>? stores = null) => new()
    {
        Id = id,
        Mills = 1700000000000,
        DefaultProfile = "Default",
        EnteredBy = "test",
        Store = (stores ?? ["Default"]).ToDictionary(name => name, _ => new ProfileData
        {
            Dia = 3.0,
            Timezone = "UTC",
            Basal = [new TimeValue { Time = "00:00", Value = 1.0 }],
            CarbRatio = [new TimeValue { Time = "00:00", Value = 10.0 }],
            Sens = [new TimeValue { Time = "00:00", Value = 50.0 }],
            TargetLow = [new TimeValue { Time = "00:00", Value = 80.0 }],
            TargetHigh = [new TimeValue { Time = "00:00", Value = 120.0 }],
        }),
    };

    [Theory]
    [InlineData("mmol")]
    [InlineData("mmol/L")]
    [InlineData("MMOL")]
    public void MergeTargets_ConvertsMmolProfilesToMgdl(string units)
    {
        // A mmol profile stores targets like low=5.0 / high=8.0; the TargetRangeEntry contract
        // is mg/dL, so they must be converted at write time (5 * 18.0182 -> 90, 8 * 18.0182 -> 144).
        var lows = new List<TimeValue> { new() { Time = "00:00", Value = 5.0 } };
        var highs = new List<TimeValue> { new() { Time = "00:00", Value = 8.0 } };

        var result = ProfileDecomposer.MergeTargets(lows, highs, units);

        result.Should().ContainSingle();
        result[0].Low.Should().Be(90);
        result[0].High.Should().Be(144);
    }

    [Theory]
    [InlineData("mg/dl")]
    [InlineData(null)]
    public void MergeTargets_LeavesMgdlProfilesUnchanged(string? units)
    {
        var lows = new List<TimeValue> { new() { Time = "00:00", Value = 80.0 } };
        var highs = new List<TimeValue> { new() { Time = "00:00", Value = 160.0 } };

        var result = ProfileDecomposer.MergeTargets(lows, highs, units);

        result.Should().ContainSingle();
        result[0].Low.Should().Be(80);
        result[0].High.Should().Be(160);
    }

    [Fact]
    public void ConvertSensitivityValues_ConvertsMmolProfilesToMgdlPerUnit()
    {
        // A mmol profile stores ISF as mmol/L per unit (e.g. 2.8); the schedule contract is
        // mg/dL per unit, so it must be converted (2.8 * 18.0182 -> 50).
        var sens = new List<TimeValue> { new() { Time = "00:00", Value = 2.8 } };

        var result = ProfileDecomposer.ConvertSensitivityValues(sens, "mmol");

        result.Should().ContainSingle();
        result[0].Value.Should().Be(50);
    }

    [Fact]
    public void ConvertSensitivityValues_LeavesMgdlProfilesUnchanged()
    {
        var sens = new List<TimeValue> { new() { Time = "00:00", Value = 50.0 } };

        var result = ProfileDecomposer.ConvertSensitivityValues(sens, "mg/dl");

        result.Should().ContainSingle();
        result[0].Value.Should().Be(50);
    }
}
