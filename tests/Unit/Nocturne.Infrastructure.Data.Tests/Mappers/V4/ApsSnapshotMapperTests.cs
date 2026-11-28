using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

public class ApsSnapshotMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var model = new ApsSnapshot
        {
            Id = id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            UtcOffset = -300,
            Device = "openaps-rig",
            LegacyId = "aps123",
            AidAlgorithm = AidAlgorithm.Loop,
            Iob = 2.5,
            BasalIob = 1.2,
            BolusIob = 1.3,
            Cob = 30.0,
            CurrentBg = 120.0,
            EventualBg = 110.0,
            TargetBg = 100.0,
            RecommendedBolus = 0.5,
            SensitivityRatio = 1.1,
            Enacted = true,
            EnactedRate = 1.5,
            EnactedDuration = 30,
            EnactedBolusVolume = 0.3,
            SuggestedJson = """{"rate":1.5}""",
            EnactedJson = """{"rate":1.5,"duration":30}""",
            PredictedDefaultJson = "[120,115,110]",
            PredictedIobJson = "[120,118,116]",
            PredictedZtJson = "[120,125,130]",
            PredictedCobJson = "[120,112,105]",
            PredictedUamJson = "[120,122,124]",
            PredictedStartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
        };

        var entity = ApsSnapshotMapper.ToEntity(model);

        entity.Id.Should().Be(id);
        entity.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime);
        entity.UtcOffset.Should().Be(-300);
        entity.Device.Should().Be("openaps-rig");
        entity.LegacyId.Should().Be("aps123");
        entity.AidAlgorithm.Should().Be("Loop");
        entity.Iob.Should().Be(2.5);
        entity.BasalIob.Should().Be(1.2);
        entity.BolusIob.Should().Be(1.3);
        entity.Cob.Should().Be(30.0);
        entity.CurrentBg.Should().Be(120.0);
        entity.EventualBg.Should().Be(110.0);
        entity.TargetBg.Should().Be(100.0);
        entity.RecommendedBolus.Should().Be(0.5);
        entity.SensitivityRatio.Should().Be(1.1);
        entity.Enacted.Should().BeTrue();
        entity.EnactedRate.Should().Be(1.5);
        entity.EnactedDuration.Should().Be(30);
        entity.EnactedBolusVolume.Should().Be(0.3);
        entity.SuggestedJson.Should().Be("""{"rate":1.5}""");
        entity.EnactedJson.Should().Be("""{"rate":1.5,"duration":30}""");
        entity.PredictedDefaultJson.Should().Be("[120,115,110]");
        entity.PredictedIobJson.Should().Be("[120,118,116]");
        entity.PredictedZtJson.Should().Be("[120,125,130]");
        entity.PredictedCobJson.Should().Be("[120,112,105]");
        entity.PredictedUamJson.Should().Be("[120,122,124]");
        entity.PredictedStartTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_EmptyGuid_GeneratesNewId()
    {
        var model = new ApsSnapshot { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime, AidAlgorithm = AidAlgorithm.OpenAps };

        var entity = ApsSnapshotMapper.ToEntity(model);

        entity.Id.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MapsAllFields()
    {
        var id = Guid.CreateVersion7();
        var createdAt = DateTime.UtcNow.AddHours(-1);
        var updatedAt = DateTime.UtcNow;
        var entity = new ApsSnapshotEntity
        {
            Id = id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            UtcOffset = -300,
            Device = "openaps-rig",
            LegacyId = "aps123",
            SysCreatedAt = createdAt,
            SysUpdatedAt = updatedAt,
            AidAlgorithm = "Loop",
            Iob = 2.5,
            BasalIob = 1.2,
            BolusIob = 1.3,
            Cob = 30.0,
            CurrentBg = 120.0,
            EventualBg = 110.0,
            TargetBg = 100.0,
            RecommendedBolus = 0.5,
            SensitivityRatio = 1.1,
            Enacted = true,
            EnactedRate = 1.5,
            EnactedDuration = 30,
            EnactedBolusVolume = 0.3,
            SuggestedJson = """{"rate":1.5}""",
            EnactedJson = """{"rate":1.5,"duration":30}""",
            PredictedDefaultJson = "[120,115,110]",
            PredictedIobJson = "[120,118,116]",
            PredictedZtJson = "[120,125,130]",
            PredictedCobJson = "[120,112,105]",
            PredictedUamJson = "[120,122,124]",
            PredictedStartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
        };

        var model = ApsSnapshotMapper.ToDomainModel(entity);

        model.Id.Should().Be(id);
        model.Mills.Should().Be(1700000000000);
        model.UtcOffset.Should().Be(-300);
        model.Device.Should().Be("openaps-rig");
        model.LegacyId.Should().Be("aps123");
        model.CreatedAt.Should().Be(createdAt);
        model.ModifiedAt.Should().Be(updatedAt);
        model.AidAlgorithm.Should().Be(AidAlgorithm.Loop);
        model.Iob.Should().Be(2.5);
        model.BasalIob.Should().Be(1.2);
        model.BolusIob.Should().Be(1.3);
        model.Cob.Should().Be(30.0);
        model.CurrentBg.Should().Be(120.0);
        model.EventualBg.Should().Be(110.0);
        model.TargetBg.Should().Be(100.0);
        model.RecommendedBolus.Should().Be(0.5);
        model.SensitivityRatio.Should().Be(1.1);
        model.Enacted.Should().BeTrue();
        model.EnactedRate.Should().Be(1.5);
        model.EnactedDuration.Should().Be(30);
        model.EnactedBolusVolume.Should().Be(0.3);
        model.SuggestedJson.Should().Be("""{"rate":1.5}""");
        model.EnactedJson.Should().Be("""{"rate":1.5,"duration":30}""");
        model.PredictedDefaultJson.Should().Be("[120,115,110]");
        model.PredictedIobJson.Should().Be("[120,118,116]");
        model.PredictedZtJson.Should().Be("[120,125,130]");
        model.PredictedCobJson.Should().Be("[120,112,105]");
        model.PredictedUamJson.Should().Be("[120,122,124]");
        model.PredictedStartMills.Should().Be(1700000000000);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateEntity_UpdatesAllFieldsExceptIdAndCreatedAt()
    {
        var originalId = Guid.CreateVersion7();
        var originalCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new ApsSnapshotEntity
        {
            Id = originalId,
            SysCreatedAt = originalCreatedAt,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime,
            AidAlgorithm = "OpenAps",
        };

        var model = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            UtcOffset = 60,
            Device = "loop-phone",
            LegacyId = "upd456",
            AidAlgorithm = AidAlgorithm.Loop,
            Iob = 3.0,
            BasalIob = 1.5,
            BolusIob = 1.5,
            Cob = 40.0,
            CurrentBg = 130.0,
            EventualBg = 100.0,
            TargetBg = 95.0,
            RecommendedBolus = 1.0,
            SensitivityRatio = 0.9,
            Enacted = true,
            EnactedRate = 2.0,
            EnactedDuration = 60,
            EnactedBolusVolume = 0.5,
            SuggestedJson = """{"temp":"absolute"}""",
            EnactedJson = """{"enacted":true}""",
            PredictedDefaultJson = "[130,125,120]",
            PredictedIobJson = "[130,128,126]",
            PredictedZtJson = "[130,135,140]",
            PredictedCobJson = "[130,122,115]",
            PredictedUamJson = "[130,132,134]",
            PredictedStartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000050000).UtcDateTime,
        };

        ApsSnapshotMapper.UpdateEntity(entity, model);

        entity.Id.Should().Be(originalId);
        entity.SysCreatedAt.Should().Be(originalCreatedAt);
        entity.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime);
        entity.UtcOffset.Should().Be(60);
        entity.Device.Should().Be("loop-phone");
        entity.LegacyId.Should().Be("upd456");
        entity.AidAlgorithm.Should().Be("Loop");
        entity.Iob.Should().Be(3.0);
        entity.BasalIob.Should().Be(1.5);
        entity.BolusIob.Should().Be(1.5);
        entity.Cob.Should().Be(40.0);
        entity.CurrentBg.Should().Be(130.0);
        entity.EventualBg.Should().Be(100.0);
        entity.TargetBg.Should().Be(95.0);
        entity.RecommendedBolus.Should().Be(1.0);
        entity.SensitivityRatio.Should().Be(0.9);
        entity.Enacted.Should().BeTrue();
        entity.EnactedRate.Should().Be(2.0);
        entity.EnactedDuration.Should().Be(60);
        entity.EnactedBolusVolume.Should().Be(0.5);
        entity.SuggestedJson.Should().Be("""{"temp":"absolute"}""");
        entity.EnactedJson.Should().Be("""{"enacted":true}""");
        entity.PredictedDefaultJson.Should().Be("[130,125,120]");
        entity.PredictedIobJson.Should().Be("[130,128,126]");
        entity.PredictedZtJson.Should().Be("[130,135,140]");
        entity.PredictedCobJson.Should().Be("[130,122,115]");
        entity.PredictedUamJson.Should().Be("[130,132,134]");
        entity.PredictedStartTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000050000).UtcDateTime);
        entity.SysUpdatedAt.Should().BeAfter(originalCreatedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_AidAlgorithmEnum_StoredAsString()
    {
        var model = new ApsSnapshot { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime, AidAlgorithm = AidAlgorithm.Loop };

        var entity = ApsSnapshotMapper.ToEntity(model);

        entity.AidAlgorithm.Should().Be("Loop");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_AidAlgorithmEnum_ParsedFromString()
    {
        var entity = new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            AidAlgorithm = "Loop",
        };

        var model = ApsSnapshotMapper.ToDomainModel(entity);

        model.AidAlgorithm.Should().Be(AidAlgorithm.Loop);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(AidAlgorithm.OpenAps, "OpenAps")]
    [InlineData(AidAlgorithm.AndroidAps, "AndroidAps")]
    [InlineData(AidAlgorithm.Loop, "Loop")]
    [InlineData(AidAlgorithm.Trio, "Trio")]
    public void ToEntity_AllAidAlgorithmValues_RoundTripCorrectly(AidAlgorithm system, string expected)
    {
        var model = new ApsSnapshot { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime, AidAlgorithm = system };

        var entity = ApsSnapshotMapper.ToEntity(model);
        entity.AidAlgorithm.Should().Be(expected);

        var roundTripped = ApsSnapshotMapper.ToDomainModel(entity);
        roundTripped.AidAlgorithm.Should().Be(system);
    }

    private const string Iob = "[120,118,116]";

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(AidAlgorithm.OpenAps)]
    [InlineData(AidAlgorithm.AndroidAps)]
    [InlineData(AidAlgorithm.IAPS)]
    public void A_default_curve_that_copies_the_iob_curve_is_stored_once_and_read_back(AidAlgorithm algorithm)
    {
        var model = new ApsSnapshot
        {
            Timestamp = DateTime.UtcNow, AidAlgorithm = algorithm, PredictedDefaultJson = Iob, PredictedIobJson = Iob,
        };

        var entity = ApsSnapshotMapper.ToEntity(model);
        var updated = new ApsSnapshotEntity { AidAlgorithm = "OpenAps", PredictedDefaultJson = "[1]" };
        ApsSnapshotMapper.UpdateEntity(updated, model);

        entity.PredictedDefaultJson.Should().BeNull();
        entity.PredictedIobJson.Should().Be(Iob);
        updated.PredictedDefaultJson.Should().BeNull();
        ApsSnapshotMapper.ToDomainModel(entity).PredictedDefaultJson.Should().Be(Iob);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void A_default_curve_that_differs_from_the_iob_curve_is_stored()
    {
        var model = new ApsSnapshot
        {
            Timestamp = DateTime.UtcNow, AidAlgorithm = AidAlgorithm.AndroidAps,
            PredictedDefaultJson = "[120,115,110]", PredictedIobJson = Iob,
        };

        var entity = ApsSnapshotMapper.ToEntity(model);

        entity.PredictedDefaultJson.Should().Be("[120,115,110]");
        ApsSnapshotMapper.ToDomainModel(entity).PredictedDefaultJson.Should().Be("[120,115,110]");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Trio_stores_a_default_curve_it_was_given_and_reads_none_from_its_iob_curve()
    {
        var given = ApsSnapshotMapper.ToEntity(new ApsSnapshot
        {
            Timestamp = DateTime.UtcNow, AidAlgorithm = AidAlgorithm.Trio, PredictedDefaultJson = Iob, PredictedIobJson = Iob,
        });
        var none = new ApsSnapshotEntity { AidAlgorithm = "Trio", PredictedIobJson = Iob };

        given.PredictedDefaultJson.Should().Be(Iob);
        ApsSnapshotMapper.ToDomainModel(none).PredictedDefaultJson.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void A_snapshot_with_a_loop_status_stores_its_default_curve_and_reads_none_from_its_iob_curve()
    {
        const string loop = """{"name":"Loop"}""";
        var given = ApsSnapshotMapper.ToEntity(new ApsSnapshot
        {
            Timestamp = DateTime.UtcNow, AidAlgorithm = AidAlgorithm.Loop, LoopJson = loop,
            PredictedDefaultJson = Iob, PredictedIobJson = Iob,
        });
        var none = new ApsSnapshotEntity { AidAlgorithm = "Loop", LoopJson = loop, PredictedIobJson = Iob };

        given.PredictedDefaultJson.Should().Be(Iob);
        ApsSnapshotMapper.ToDomainModel(none).PredictedDefaultJson.Should().BeNull();
    }
}
