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
            Mills = 1700000000000,
            UtcOffset = -300,
            Device = "openaps-rig",
            LegacyId = "aps123",
            ApsSystem = ApsSystem.Loop,
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
            PredictedStartMills = 1700000000000,
        };

        var entity = ApsSnapshotMapper.ToEntity(model);

        entity.Id.Should().Be(id);
        entity.Mills.Should().Be(1700000000000);
        entity.UtcOffset.Should().Be(-300);
        entity.Device.Should().Be("openaps-rig");
        entity.LegacyId.Should().Be("aps123");
        entity.ApsSystem.Should().Be("Loop");
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
        entity.PredictedStartMills.Should().Be(1700000000000);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_EmptyGuid_GeneratesNewId()
    {
        var model = new ApsSnapshot { Mills = 1700000000000, ApsSystem = ApsSystem.OpenAps };

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
            Mills = 1700000000000,
            UtcOffset = -300,
            Device = "openaps-rig",
            LegacyId = "aps123",
            SysCreatedAt = createdAt,
            SysUpdatedAt = updatedAt,
            ApsSystem = "Loop",
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
            PredictedStartMills = 1700000000000,
        };

        var model = ApsSnapshotMapper.ToDomainModel(entity);

        model.Id.Should().Be(id);
        model.Mills.Should().Be(1700000000000);
        model.UtcOffset.Should().Be(-300);
        model.Device.Should().Be("openaps-rig");
        model.LegacyId.Should().Be("aps123");
        model.CreatedAt.Should().Be(createdAt);
        model.ModifiedAt.Should().Be(updatedAt);
        model.ApsSystem.Should().Be(ApsSystem.Loop);
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
            Mills = 1000,
            ApsSystem = "OpenAps",
        };

        var model = new ApsSnapshot
        {
            Mills = 1700000000000,
            UtcOffset = 60,
            Device = "loop-phone",
            LegacyId = "upd456",
            ApsSystem = ApsSystem.Loop,
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
            PredictedStartMills = 1700000050000,
        };

        ApsSnapshotMapper.UpdateEntity(entity, model);

        entity.Id.Should().Be(originalId);
        entity.SysCreatedAt.Should().Be(originalCreatedAt);
        entity.Mills.Should().Be(1700000000000);
        entity.UtcOffset.Should().Be(60);
        entity.Device.Should().Be("loop-phone");
        entity.LegacyId.Should().Be("upd456");
        entity.ApsSystem.Should().Be("Loop");
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
        entity.PredictedStartMills.Should().Be(1700000050000);
        entity.SysUpdatedAt.Should().BeAfter(originalCreatedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToEntity_ApsSystemEnum_StoredAsString()
    {
        var model = new ApsSnapshot { Mills = 1700000000000, ApsSystem = ApsSystem.Loop };

        var entity = ApsSnapshotMapper.ToEntity(model);

        entity.ApsSystem.Should().Be("Loop");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_ApsSystemEnum_ParsedFromString()
    {
        var entity = new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(),
            Mills = 1700000000000,
            ApsSystem = "Loop",
        };

        var model = ApsSnapshotMapper.ToDomainModel(entity);

        model.ApsSystem.Should().Be(ApsSystem.Loop);
    }
}
