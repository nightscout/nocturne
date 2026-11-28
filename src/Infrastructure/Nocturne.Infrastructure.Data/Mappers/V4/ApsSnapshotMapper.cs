using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between ApsSnapshot domain models and ApsSnapshotEntity database entities
/// </summary>
public static class ApsSnapshotMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of ApsSnapshotEntity.</returns>
    public static ApsSnapshotEntity ToEntity(ApsSnapshot model)
    {
        return new ApsSnapshotEntity
        {
            DeviceId = model.DeviceId,
            PatientDeviceId = model.PatientDeviceId,
            SyncIdentifier = model.SyncIdentifier,
            AidAlgorithm = model.AidAlgorithm.ToString(),
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
            PredictedDefaultJson = StoredDefaultCurve(model),
            PredictedIobJson = model.PredictedIobJson,
            PredictedZtJson = model.PredictedZtJson,
            PredictedCobJson = model.PredictedCobJson,
            PredictedUamJson = model.PredictedUamJson,
            PredictedStartTimestamp = model.PredictedStartTimestamp,
            LoopJson = model.LoopJson,
            OverrideJson = model.OverrideJson,
            AidVersion = model.AidVersion,
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of ApsSnapshot domain model.</returns>
    public static ApsSnapshot ToDomainModel(ApsSnapshotEntity entity)
    {
        return new ApsSnapshot
        {
            DeviceId = entity.DeviceId,
            PatientDeviceId = entity.PatientDeviceId,
            SyncIdentifier = entity.SyncIdentifier,
            AidAlgorithm = Enum.TryParse<AidAlgorithm>(entity.AidAlgorithm, out var sys) ? sys : AidAlgorithm.Unknown,
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
            ClosedLoop = entity.ClosedLoop,
            EnactedRate = entity.EnactedRate,
            EnactedDuration = entity.EnactedDuration,
            EnactedBolusVolume = entity.EnactedBolusVolume,
            SuggestedJson = entity.SuggestedJson,
            EnactedJson = entity.EnactedJson,
            PredictedDefaultJson = entity.PredictedDefaultJson
                ?? (DefaultIsIobCurve(entity.AidAlgorithm, entity.LoopJson) ? entity.PredictedIobJson : null),
            PredictedIobJson = entity.PredictedIobJson,
            PredictedZtJson = entity.PredictedZtJson,
            PredictedCobJson = entity.PredictedCobJson,
            PredictedUamJson = entity.PredictedUamJson,
            PredictedStartTimestamp = entity.PredictedStartTimestamp,
            LoopJson = entity.LoopJson,
            OverrideJson = entity.OverrideJson,
            AidVersion = entity.AidVersion,
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(ApsSnapshotEntity entity, ApsSnapshot model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.DeviceId = model.DeviceId;
        entity.PatientDeviceId = model.PatientDeviceId;
        entity.SyncIdentifier = model.SyncIdentifier;
        entity.AidAlgorithm = model.AidAlgorithm.ToString();
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
        entity.PredictedDefaultJson = StoredDefaultCurve(model);
        entity.PredictedIobJson = model.PredictedIobJson;
        entity.PredictedZtJson = model.PredictedZtJson;
        entity.PredictedCobJson = model.PredictedCobJson;
        entity.PredictedUamJson = model.PredictedUamJson;
        entity.PredictedStartTimestamp = model.PredictedStartTimestamp;
        entity.LoopJson = model.LoopJson;
        entity.OverrideJson = model.OverrideJson;
        entity.AidVersion = model.AidVersion;
    }

    /// <summary>
    /// Whether a snapshot's default curve is its IOB curve: true for every snapshot without a Loop status
    /// but Trio's, since <c>ApsSnapshotCurves.FillColumns</c> fills an empty default from the IOB curve
    /// for each of them. Such a snapshot stores its default only when it differs from the IOB curve, and
    /// reads the IOB curve back as its default when it stores none.
    /// <see cref="ApsSnapshotCurvesRewriter"/> clears the stored copies.
    /// </summary>
    internal static bool DefaultIsIobCurve(string aidAlgorithm, string? loopJson) =>
        aidAlgorithm != nameof(AidAlgorithm.Trio) && string.IsNullOrEmpty(loopJson);

    private static string? StoredDefaultCurve(ApsSnapshot model) =>
        model.PredictedDefaultJson is { } curve
            && model.PredictedIobJson is { } iob
            && DefaultIsIobCurve(model.AidAlgorithm.ToString(), model.LoopJson)
            && SameJson(curve, iob)
            ? null
            : model.PredictedDefaultJson;

    // Compared as JSON, as the rewriter's jsonb equality does: the columns are jsonb, so a curve reads
    // back in PostgreSQL's formatting whichever text was written.
    private static bool SameJson(string a, string b)
    {
        if (a == b)
            return true;

        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
