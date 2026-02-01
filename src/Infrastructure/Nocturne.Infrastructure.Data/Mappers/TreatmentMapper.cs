using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Common;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between Treatment domain models and TreatmentEntity database entities
/// </summary>
public static class TreatmentMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static TreatmentEntity ToEntity(Treatment treatment)
    {
        return new TreatmentEntity
        {
            Id = string.IsNullOrEmpty(treatment.Id)
                ? Guid.CreateVersion7()
                : ParseIdToGuid(treatment.Id),
            OriginalId = IsValidMongoObjectId(treatment.Id) ? treatment.Id : null,
            EventType = treatment.EventType,
            Reason = treatment.Reason,
            Glucose = treatment.Glucose,
            GlucoseType = treatment.GlucoseType,
            Carbs = treatment.Carbs,
            Insulin = treatment.Insulin,
            Protein = treatment.Protein,
            Fat = treatment.Fat,
            FoodType = treatment.FoodType,
            Units = treatment.Units,
            Mills = treatment.Mills,
            Created_at = treatment.Created_at,
            Duration = treatment.Duration,
            Percent = treatment.Percent,
            Absolute = treatment.Absolute,
            Notes = treatment.Notes,
            EnteredBy = treatment.EnteredBy,
            TargetTop = treatment.TargetTop,
            TargetBottom = treatment.TargetBottom,
            Profile = treatment.Profile,
            Split = treatment.Split,
            Date = treatment.Date,
            CarbTime = treatment.CarbTime,
            BolusCalcJson =
                treatment.BolusCalc != null ? JsonSerializer.Serialize(treatment.BolusCalc) : null,
            UtcOffset = treatment.UtcOffset,
            Timestamp = ParseTimestampToLong(treatment.Timestamp),
            CuttedBy = treatment.CuttedBy,
            Cutting = treatment.Cutting,
            EventTime = treatment.EventTime,
            PreBolus = treatment.PreBolus,
            Rate = treatment.Rate,
            Mgdl = treatment.Mgdl,
            Mmol = treatment.Mmol,
            EndMills = treatment.EndMills,
            DurationType = treatment.DurationType,
            IsAnnouncement = treatment.IsAnnouncement,
            ProfileJson = treatment.ProfileJson,
            EndProfile = treatment.EndProfile,
            InsulinNeedsScaleFactor = treatment.InsulinNeedsScaleFactor,
            AbsorptionTime = treatment.AbsorptionTime,
            EnteredInsulin = treatment.EnteredInsulin,
            SplitNow = treatment.SplitNow,
            SplitExt = treatment.SplitExt,
            Status = treatment.Status,
            Relative = treatment.Relative,
            CR = treatment.CR,
            NsClientId = treatment.NsClientId,
            First = treatment.First,
            End = treatment.End,
            CircadianPercentageProfile = treatment.CircadianPercentageProfile,
            Percentage = treatment.Percentage,
            Timeshift = treatment.Timeshift,
            TransmitterId = treatment.TransmitterId,
            DataSource = treatment.DataSource,
            InsulinRecommendationForCarbs = treatment.InsulinRecommendationForCarbs,
            InsulinRecommendationForCorrection = treatment.InsulinRecommendationForCorrection,
            InsulinProgrammed = treatment.InsulinProgrammed,
            InsulinDelivered = treatment.InsulinDelivered,
            InsulinOnBoard = treatment.InsulinOnBoard,
            BloodGlucoseInput = treatment.BloodGlucoseInput,
            BloodGlucoseInputSource = treatment.BloodGlucoseInputSource,
            CalculationType = treatment.CalculationType?.ToString(),
            AdditionalPropertiesJson =
                treatment.AdditionalProperties != null
                    ? JsonSerializer.Serialize(treatment.AdditionalProperties)
                    : null,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static Treatment ToDomainModel(TreatmentEntity entity)
    {
        return new Treatment
        {
            Id = entity.OriginalId ?? entity.Id.ToString(),
            DbId = entity.Id,
            EventType = entity.EventType,
            Reason = entity.Reason,
            Glucose = entity.Glucose,
            GlucoseType = entity.GlucoseType,
            Carbs = entity.Carbs,
            Insulin = entity.Insulin,
            Protein = entity.Protein,
            Fat = entity.Fat,
            FoodType = entity.FoodType,
            Units = entity.Units,
            Mills = entity.Mills,
            Created_at = entity.Created_at,
            Duration = entity.Duration,
            Percent = entity.Percent,
            Absolute = entity.Absolute,
            Notes = entity.Notes,
            EnteredBy = entity.EnteredBy,
            TargetTop = entity.TargetTop,
            TargetBottom = entity.TargetBottom,
            Profile = entity.Profile,
            Split = entity.Split,
            Date = entity.Date,
            CarbTime = entity.CarbTime,
            BolusCalc = DeserializeJsonProperty<Dictionary<string, object>>(entity.BolusCalcJson),
            UtcOffset = entity.UtcOffset,
            Timestamp = FormatTimestampToString(entity.Timestamp),
            CuttedBy = entity.CuttedBy,
            Cutting = entity.Cutting,
            EventTime = entity.EventTime,
            PreBolus = entity.PreBolus,
            Rate = entity.Rate,
            Mgdl = entity.Mgdl,
            Mmol = entity.Mmol,
            EndMills = entity.EndMills,
            DurationType = entity.DurationType,
            IsAnnouncement = entity.IsAnnouncement,
            ProfileJson = entity.ProfileJson,
            EndProfile = entity.EndProfile,
            InsulinNeedsScaleFactor = entity.InsulinNeedsScaleFactor,
            AbsorptionTime = entity.AbsorptionTime,
            EnteredInsulin = entity.EnteredInsulin,
            SplitNow = entity.SplitNow,
            SplitExt = entity.SplitExt,
            Status = entity.Status,
            Relative = entity.Relative,
            CR = entity.CR,
            NsClientId = entity.NsClientId,
            First = entity.First,
            End = entity.End,
            CircadianPercentageProfile = entity.CircadianPercentageProfile,
            Percentage = entity.Percentage,
            Timeshift = entity.Timeshift,
            TransmitterId = entity.TransmitterId,
            DataSource = entity.DataSource,
            InsulinRecommendationForCarbs = entity.InsulinRecommendationForCarbs,
            InsulinRecommendationForCorrection = entity.InsulinRecommendationForCorrection,
            InsulinProgrammed = entity.InsulinProgrammed,
            InsulinDelivered = entity.InsulinDelivered,
            InsulinOnBoard = entity.InsulinOnBoard,
            BloodGlucoseInput = entity.BloodGlucoseInput,
            BloodGlucoseInputSource = entity.BloodGlucoseInputSource,
            CalculationType = Enum.TryParse<CalculationType>(entity.CalculationType, out var calcType) ? calcType : null,
            AdditionalProperties = DeserializeJsonProperty<Dictionary<string, object>>(
                entity.AdditionalPropertiesJson
            ),
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(TreatmentEntity entity, Treatment treatment)
    {
        entity.EventType = treatment.EventType;
        entity.Reason = treatment.Reason;
        entity.Glucose = treatment.Glucose;
        entity.GlucoseType = treatment.GlucoseType;
        entity.Carbs = treatment.Carbs;
        entity.Insulin = treatment.Insulin;
        entity.Protein = treatment.Protein;
        entity.Fat = treatment.Fat;
        entity.FoodType = treatment.FoodType;
        entity.Units = treatment.Units;
        entity.Mills = treatment.Mills;
        entity.Created_at = treatment.Created_at;
        entity.Duration = treatment.Duration;
        entity.Percent = treatment.Percent;
        entity.Absolute = treatment.Absolute;
        entity.Notes = treatment.Notes;
        entity.EnteredBy = treatment.EnteredBy;
        entity.TargetTop = treatment.TargetTop;
        entity.TargetBottom = treatment.TargetBottom;
        entity.Profile = treatment.Profile;
        entity.Split = treatment.Split;
        entity.Date = treatment.Date;
        entity.CarbTime = treatment.CarbTime;
        entity.BolusCalcJson =
            treatment.BolusCalc != null ? JsonSerializer.Serialize(treatment.BolusCalc) : null;
        entity.UtcOffset = treatment.UtcOffset;
        entity.Timestamp = ParseTimestampToLong(treatment.Timestamp);
        entity.CuttedBy = treatment.CuttedBy;
        entity.Cutting = treatment.Cutting;
        entity.EventTime = treatment.EventTime;
        entity.PreBolus = treatment.PreBolus;
        entity.Rate = treatment.Rate;
        entity.Mgdl = treatment.Mgdl;
        entity.Mmol = treatment.Mmol;
        entity.EndMills = treatment.EndMills;
        entity.DurationType = treatment.DurationType;
        entity.IsAnnouncement = treatment.IsAnnouncement;
        entity.ProfileJson = treatment.ProfileJson;
        entity.EndProfile = treatment.EndProfile;
        entity.InsulinNeedsScaleFactor = treatment.InsulinNeedsScaleFactor;
        entity.AbsorptionTime = treatment.AbsorptionTime;
        entity.EnteredInsulin = treatment.EnteredInsulin;
        entity.SplitNow = treatment.SplitNow;
        entity.SplitExt = treatment.SplitExt;
        entity.Status = treatment.Status;
        entity.Relative = treatment.Relative;
        entity.CR = treatment.CR;
        entity.NsClientId = treatment.NsClientId;
        entity.First = treatment.First;
        entity.End = treatment.End;
        entity.CircadianPercentageProfile = treatment.CircadianPercentageProfile;
        entity.Percentage = treatment.Percentage;
        entity.Timeshift = treatment.Timeshift;
        entity.TransmitterId = treatment.TransmitterId;
        entity.DataSource = treatment.DataSource;
        entity.InsulinRecommendationForCarbs = treatment.InsulinRecommendationForCarbs;
        entity.InsulinRecommendationForCorrection = treatment.InsulinRecommendationForCorrection;
        entity.InsulinProgrammed = treatment.InsulinProgrammed;
        entity.InsulinDelivered = treatment.InsulinDelivered;
        entity.InsulinOnBoard = treatment.InsulinOnBoard;
        entity.BloodGlucoseInput = treatment.BloodGlucoseInput;
        entity.BloodGlucoseInputSource = treatment.BloodGlucoseInputSource;
        entity.CalculationType = treatment.CalculationType?.ToString();
        entity.AdditionalPropertiesJson =
            treatment.AdditionalProperties != null
                ? JsonSerializer.Serialize(treatment.AdditionalProperties)
                : null;
    }

    /// <summary>
    /// Check if string is a valid MongoDB ObjectId (24 hex characters)
    /// </summary>
    private static bool IsValidMongoObjectId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 24)
            return false;

        foreach (char c in id)
        {
            if (!((c >= '0' && c <= '9') ||
                  (c >= 'a' && c <= 'f') ||
                  (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Parse string ID to GUID, or generate new GUID if invalid
    /// </summary>
    private static Guid ParseIdToGuid(string id)
    {
        // Hash the ID to get a deterministic GUID for consistent mapping
        // This ensures the same string ID always maps to the same GUID
        if (string.IsNullOrEmpty(id))
            return Guid.CreateVersion7();

        if (Guid.TryParse(id, out var parsedGuid))
        {
            return parsedGuid;
        }

        try
        {
            // Use a simple hash of the ID to generate a consistent GUID
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var hashBytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(id));
            var guidBytes = new byte[16];
            Array.Copy(hashBytes, guidBytes, 16);
            return new Guid(guidBytes);
        }
        catch
        {
            // If anything fails, generate a new GUID
            return Guid.CreateVersion7();
        }
    }

    /// <summary>
    /// Safely deserialize JSON property
    /// </summary>
    private static T? DeserializeJsonProperty<T>(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null")
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Parse ISO 8601 timestamp string to Unix milliseconds
    /// </summary>
    private static long? ParseTimestampToLong(string? timestamp)
    {
        if (string.IsNullOrEmpty(timestamp))
            return null;

        if (
            DateTime.TryParse(
                timestamp,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var dateTime
            )
        )
        {
            return (
                (DateTimeOffset)DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            ).ToUnixTimeMilliseconds();
        }

        return null;
    }

    /// <summary>
    /// Format Unix milliseconds to ISO 8601 timestamp string
    /// </summary>
    private static string? FormatTimestampToString(long? timestamp)
    {
        if (!timestamp.HasValue)
            return null;

        return DateTimeOffset
            .FromUnixTimeMilliseconds(timestamp.Value)
            .ToString("yyyy-MM-ddTHH:mm:ssZ");
    }
}
