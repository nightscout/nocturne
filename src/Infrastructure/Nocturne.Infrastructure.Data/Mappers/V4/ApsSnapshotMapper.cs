using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between ApsSnapshot domain models and ApsSnapshotEntity database entities
/// </summary>
public static class ApsSnapshotMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static ApsSnapshotEntity ToEntity(ApsSnapshot model)
    {
        return new ApsSnapshotEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Mills = model.Mills,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            ApsSystem = model.ApsSystem.ToString(),
            Iob = model.Iob,
            BasalIob = model.BasalIob,
            BolusIob = model.BolusIob,
            Cob = model.Cob,
            CurrentBg = model.CurrentBg,
            EventualBg = model.EventualBg,
            TargetBg = model.TargetBg,
            RecommendedBolus = model.RecommendedBolus,
            SensitivityRatio = model.SensitivityRatio,
            Enacted = model.Enacted,
            EnactedRate = model.EnactedRate,
            EnactedDuration = model.EnactedDuration,
            EnactedBolusVolume = model.EnactedBolusVolume,
            SuggestedJson = model.SuggestedJson,
            EnactedJson = model.EnactedJson,
            PredictedDefaultJson = model.PredictedDefaultJson,
            PredictedIobJson = model.PredictedIobJson,
            PredictedZtJson = model.PredictedZtJson,
            PredictedCobJson = model.PredictedCobJson,
            PredictedUamJson = model.PredictedUamJson,
            PredictedStartMills = model.PredictedStartMills,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static ApsSnapshot ToDomainModel(ApsSnapshotEntity entity)
    {
        return new ApsSnapshot
        {
            Id = entity.Id,
            Mills = entity.Mills,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            ApsSystem = Enum.TryParse<ApsSystem>(entity.ApsSystem, out var sys) ? sys : ApsSystem.OpenAps,
            Iob = entity.Iob,
            BasalIob = entity.BasalIob,
            BolusIob = entity.BolusIob,
            Cob = entity.Cob,
            CurrentBg = entity.CurrentBg,
            EventualBg = entity.EventualBg,
            TargetBg = entity.TargetBg,
            RecommendedBolus = entity.RecommendedBolus,
            SensitivityRatio = entity.SensitivityRatio,
            Enacted = entity.Enacted,
            EnactedRate = entity.EnactedRate,
            EnactedDuration = entity.EnactedDuration,
            EnactedBolusVolume = entity.EnactedBolusVolume,
            SuggestedJson = entity.SuggestedJson,
            EnactedJson = entity.EnactedJson,
            PredictedDefaultJson = entity.PredictedDefaultJson,
            PredictedIobJson = entity.PredictedIobJson,
            PredictedZtJson = entity.PredictedZtJson,
            PredictedCobJson = entity.PredictedCobJson,
            PredictedUamJson = entity.PredictedUamJson,
            PredictedStartMills = entity.PredictedStartMills,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(ApsSnapshotEntity entity, ApsSnapshot model)
    {
        entity.Mills = model.Mills;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.ApsSystem = model.ApsSystem.ToString();
        entity.Iob = model.Iob;
        entity.BasalIob = model.BasalIob;
        entity.BolusIob = model.BolusIob;
        entity.Cob = model.Cob;
        entity.CurrentBg = model.CurrentBg;
        entity.EventualBg = model.EventualBg;
        entity.TargetBg = model.TargetBg;
        entity.RecommendedBolus = model.RecommendedBolus;
        entity.SensitivityRatio = model.SensitivityRatio;
        entity.Enacted = model.Enacted;
        entity.EnactedRate = model.EnactedRate;
        entity.EnactedDuration = model.EnactedDuration;
        entity.EnactedBolusVolume = model.EnactedBolusVolume;
        entity.SuggestedJson = model.SuggestedJson;
        entity.EnactedJson = model.EnactedJson;
        entity.PredictedDefaultJson = model.PredictedDefaultJson;
        entity.PredictedIobJson = model.PredictedIobJson;
        entity.PredictedZtJson = model.PredictedZtJson;
        entity.PredictedCobJson = model.PredictedCobJson;
        entity.PredictedUamJson = model.PredictedUamJson;
        entity.PredictedStartMills = model.PredictedStartMills;
    }
}
