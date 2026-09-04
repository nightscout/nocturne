using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Services.V4;

public class ProfileDecomposerTests
{
    /// <summary>
    /// Profiles persist ONLY as the five decomposed granular records, so on the HTTP path
    /// (v1/v3 profile create/update) their audit rows are the entire mutation trail for a
    /// user's profile edit. DecomposeAsync must NOT push a SystemAuditScope — connector
    /// re-syncs are suppressed by the sync scope's system audit context instead, and
    /// byte-identical re-upserts diff to empty and are skipped — see
    /// <see cref="DecomposeAsync_KeepsStoredCorrelationId_WhenRecordsAlreadyExist"/> for the one
    /// column that otherwise stops that being true.
    /// </summary>
    [Fact]
    public async Task DecomposeAsync_PreservesCallerAuditAttribution()
    {
        var auditContext = new AuditContext { AuthType = "ApiKey", SubjectName = "someone" };

        var attributionDuringUpsert = new List<(bool IsSystem, string? AuthType)>();
        void Capture() => attributionDuringUpsert.Add((auditContext.IsSystem, auditContext.AuthType));

        var therapySettingsRepo = new Mock<ITherapySettingsRepository>();
        therapySettingsRepo
            .Setup(x => x.CreateAsync(It.IsAny<TherapySettings>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(Capture)
            .ReturnsAsync((TherapySettings m, WriteOrigin _, CancellationToken _) => m);

        var basalScheduleRepo = new Mock<IBasalScheduleRepository>();
        basalScheduleRepo
            .Setup(x => x.CreateAsync(It.IsAny<BasalSchedule>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(Capture)
            .ReturnsAsync((BasalSchedule m, WriteOrigin _, CancellationToken _) => m);

        var carbRatioScheduleRepo = new Mock<ICarbRatioScheduleRepository>();
        carbRatioScheduleRepo
            .Setup(x => x.CreateAsync(It.IsAny<CarbRatioSchedule>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(Capture)
            .ReturnsAsync((CarbRatioSchedule m, WriteOrigin _, CancellationToken _) => m);

        var sensitivityScheduleRepo = new Mock<ISensitivityScheduleRepository>();
        sensitivityScheduleRepo
            .Setup(x => x.CreateAsync(It.IsAny<SensitivitySchedule>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(Capture)
            .ReturnsAsync((SensitivitySchedule m, WriteOrigin _, CancellationToken _) => m);

        var targetRangeScheduleRepo = new Mock<ITargetRangeScheduleRepository>();
        targetRangeScheduleRepo
            .Setup(x => x.CreateAsync(It.IsAny<TargetRangeSchedule>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(Capture)
            .ReturnsAsync((TargetRangeSchedule m, WriteOrigin _, CancellationToken _) => m);

        var decomposer = new ProfileDecomposer(
            therapySettingsRepo.Object,
            basalScheduleRepo.Object,
            carbRatioScheduleRepo.Object,
            sensitivityScheduleRepo.Object,
            targetRangeScheduleRepo.Object,
            NullLogger<ProfileDecomposer>.Instance);

        var profile = new Profile
        {
            Id = "profile1",
            Mills = 1700000000000,
            DefaultProfile = "Default",
            EnteredBy = "test",
            Store = new Dictionary<string, ProfileData>
            {
                ["Default"] = new ProfileData
                {
                    Dia = 3.0,
                    Timezone = "UTC",
                    Basal = [new TimeValue { Time = "00:00", Value = 1.0 }],
                    CarbRatio = [new TimeValue { Time = "00:00", Value = 10.0 }],
                    Sens = [new TimeValue { Time = "00:00", Value = 50.0 }],
                    TargetLow = [new TimeValue { Time = "00:00", Value = 80.0 }],
                    TargetHigh = [new TimeValue { Time = "00:00", Value = 120.0 }],
                },
            },
        };

        var result = await decomposer.DecomposeAsync(profile, WriteOrigin.Live);

        result.CreatedRecords.Should().HaveCount(5);
        attributionDuringUpsert.Should().HaveCount(5).And.AllSatisfy(a =>
        {
            a.IsSystem.Should().BeFalse("a user's profile edit must stay user-attributed in the audit log");
            a.AuthType.Should().Be("ApiKey");
        });
    }

    /// <summary>
    /// A decomposer mints a fresh correlation id per call, so re-upserting an unchanged profile
    /// rewrote correlation_id on all five siblings. Such an update carries no material change, so it
    /// neither audits nor broadcasts — but the column is indexed, so the update cannot be HOT and
    /// appends an entry to every index on the table. Because the id is UUID v7 the appends are
    /// monotonic and never reuse freed space, which took production to 1.24% leaf density. The
    /// stored id has to survive an update for an unchanged re-upsert to diff to nothing at all.
    /// </summary>
    [Fact]
    public async Task DecomposeAsync_KeepsStoredCorrelationId_WhenRecordsAlreadyExist()
    {
        var stored = Guid.CreateVersion7();

        var therapySettingsRepo = ExistingRecord<ITherapySettingsRepository, TherapySettings>(stored, out var therapyUpdates);
        var basalScheduleRepo = ExistingRecord<IBasalScheduleRepository, BasalSchedule>(stored, out var basalUpdates);
        var carbRatioScheduleRepo = ExistingRecord<ICarbRatioScheduleRepository, CarbRatioSchedule>(stored, out var carbRatioUpdates);
        var sensitivityScheduleRepo = ExistingRecord<ISensitivityScheduleRepository, SensitivitySchedule>(stored, out var sensitivityUpdates);
        var targetRangeScheduleRepo = ExistingRecord<ITargetRangeScheduleRepository, TargetRangeSchedule>(stored, out var targetRangeUpdates);

        var decomposer = new ProfileDecomposer(
            therapySettingsRepo.Object,
            basalScheduleRepo.Object,
            carbRatioScheduleRepo.Object,
            sensitivityScheduleRepo.Object,
            targetRangeScheduleRepo.Object,
            NullLogger<ProfileDecomposer>.Instance);

        var result = await decomposer.DecomposeAsync(BuildProfile(), WriteOrigin.Live);

        result.UpdatedRecords.Should().HaveCount(5);

        var written = therapyUpdates.Cast<IV4Record>()
            .Concat(basalUpdates)
            .Concat(carbRatioUpdates)
            .Concat(sensitivityUpdates)
            .Concat(targetRangeUpdates)
            .ToList();

        written.Should().HaveCount(5);
        written.Should().AllSatisfy(record => record.CorrelationId.Should().Be(
            stored,
            "an unchanged re-upsert must leave correlation_id alone, or every sync writes a new row version into every index"));
    }

    /// <summary>
    /// A repository already holding one record under any legacy id, capturing the models handed to
    /// <c>UpdateAsync</c> so a test can assert what would actually have been written.
    /// </summary>
    private static Mock<TRepo> ExistingRecord<TRepo, TRecord>(Guid storedCorrelationId, out List<TRecord> updates)
        where TRepo : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record, new()
    {
        var captured = new List<TRecord>();
        updates = captured;

        var repo = new Mock<TRepo>();
        repo
            .Setup(x => x.GetByLegacyIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new TRecord { Id = Guid.CreateVersion7(), CorrelationId = storedCorrelationId });
        repo
            .Setup(x => x.UpdateAsync(It.IsAny<Guid>(), It.IsAny<TRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, TRecord m, WriteOrigin _, CancellationToken _) => captured.Add(m))
            .ReturnsAsync((Guid _, TRecord m, WriteOrigin _, CancellationToken _) => m);
        return repo;
    }

    private static Profile BuildProfile() => new()
    {
        Id = "profile1",
        Mills = 1700000000000,
        DefaultProfile = "Default",
        EnteredBy = "test",
        Store = new Dictionary<string, ProfileData>
        {
            ["Default"] = new ProfileData
            {
                Dia = 3.0,
                Timezone = "UTC",
                Basal = [new TimeValue { Time = "00:00", Value = 1.0 }],
                CarbRatio = [new TimeValue { Time = "00:00", Value = 10.0 }],
                Sens = [new TimeValue { Time = "00:00", Value = 50.0 }],
                TargetLow = [new TimeValue { Time = "00:00", Value = 80.0 }],
                TargetHigh = [new TimeValue { Time = "00:00", Value = 120.0 }],
            },
        },
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
