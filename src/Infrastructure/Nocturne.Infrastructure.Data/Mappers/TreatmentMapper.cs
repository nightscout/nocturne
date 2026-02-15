using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Common;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.OwnedTypes;

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
            Carbs = treatment.Carbs,
            Insulin = treatment.Insulin,
            Duration = treatment.Duration,
            Mills = treatment.Mills,
            Created_at = treatment.Created_at,
            Date = treatment.Date,
            Timestamp = ParseTimestampToLong(treatment.Timestamp),
            UtcOffset = treatment.UtcOffset,
            Notes = treatment.Notes,
            EnteredBy = treatment.EnteredBy,
            Status = treatment.Status,
            TargetTop = treatment.TargetTop,
            TargetBottom = treatment.TargetBottom,
            Split = treatment.Split,
            CuttedBy = treatment.CuttedBy,
            Cutting = treatment.Cutting,
            NsClientId = treatment.NsClientId,
            First = treatment.First,
            End = treatment.End,
            TransmitterId = treatment.TransmitterId,
            EventTime = treatment.EventTime,
            IsAnnouncement = treatment.IsAnnouncement,
            DataSource = treatment.DataSource,
            AdditionalPropertiesJson =
                treatment.AdditionalProperties != null
                    ? JsonSerializer.Serialize(treatment.AdditionalProperties)
                    : null,

            GlucoseData = new TreatmentGlucoseData
            {
                Glucose = treatment.Glucose,
                GlucoseType = treatment.GlucoseType,
                Mgdl = treatment.Mgdl,
                Mmol = treatment.Mmol,
                Units = treatment.Units,
            },
            Nutritional = new TreatmentNutritionalData
            {
                Protein = treatment.Protein,
                Fat = treatment.Fat,
                FoodType = treatment.FoodType,
                CarbTime = treatment.CarbTime,
                AbsorptionTime = treatment.AbsorptionTime,
            },
            Basal = new TreatmentBasalData
            {
                Rate = treatment.Rate,
                Percent = treatment.Percent,
                Absolute = treatment.Absolute,
                Relative = treatment.Relative,
                DurationType = treatment.DurationType,
                EndMills = treatment.EndMills,
                DurationInMilliseconds = treatment.DurationInMilliseconds,
            },
            BolusCalc = new TreatmentBolusCalcData
            {
                InsulinRecommendationForCarbs = treatment.InsulinRecommendationForCarbs,
                InsulinRecommendationForCorrection = treatment.InsulinRecommendationForCorrection,
                InsulinProgrammed = treatment.InsulinProgrammed,
                InsulinDelivered = treatment.InsulinDelivered,
                InsulinOnBoard = treatment.InsulinOnBoard,
                BloodGlucoseInput = treatment.BloodGlucoseInput,
                BloodGlucoseInputSource = treatment.BloodGlucoseInputSource,
                CalculationType = treatment.CalculationType?.ToString(),
                BolusCalcJson = treatment.BolusCalc != null
                    ? JsonSerializer.Serialize(treatment.BolusCalc)
                    : null,
                BolusCalculatorResult = treatment.BolusCalculatorResult,
                EnteredInsulin = treatment.EnteredInsulin,
                SplitNow = treatment.SplitNow,
                SplitExt = treatment.SplitExt,
                CR = treatment.CR,
                PreBolus = treatment.PreBolus,
            },
            ProfileData = new TreatmentProfileData
            {
                Profile = treatment.Profile,
                ProfileJson = treatment.ProfileJson,
                EndProfile = treatment.EndProfile,
                CircadianPercentageProfile = treatment.CircadianPercentageProfile,
                Percentage = treatment.Percentage,
                Timeshift = treatment.Timeshift,
                InsulinNeedsScaleFactor = treatment.InsulinNeedsScaleFactor,
            },
            Aaps = new TreatmentAapsData
            {
                PumpId = treatment.PumpId,
                PumpSerial = treatment.PumpSerial,
                PumpType = treatment.PumpType,
                EndId = treatment.EndId,
                IsValid = treatment.IsValid,
                IsReadOnly = treatment.IsReadOnly,
                IsBasalInsulin = treatment.IsBasalInsulin,
                OriginalDuration = treatment.OriginalDuration,
                OriginalProfileName = treatment.OriginalProfileName,
                OriginalPercentage = treatment.OriginalPercentage,
                OriginalTimeshift = treatment.OriginalTimeshift,
                OriginalCustomizedName = treatment.OriginalCustomizedName,
                OriginalEnd = treatment.OriginalEnd,
            },
            Loop = new TreatmentLoopData
            {
                RemoteCarbs = treatment.RemoteCarbs,
                RemoteAbsorption = treatment.RemoteAbsorption,
                RemoteBolus = treatment.RemoteBolus,
                Otp = treatment.Otp,
                ReasonDisplay = treatment.ReasonDisplay,
            },
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
            Carbs = entity.Carbs,
            Insulin = entity.Insulin,
            Duration = entity.Duration,
            Mills = entity.Mills,
            Created_at = entity.Created_at,
            Date = entity.Date,
            Timestamp = FormatTimestampToString(entity.Timestamp),
            UtcOffset = entity.UtcOffset,
            Notes = entity.Notes,
            EnteredBy = entity.EnteredBy,
            Status = entity.Status,
            TargetTop = entity.TargetTop,
            TargetBottom = entity.TargetBottom,
            Split = entity.Split,
            CuttedBy = entity.CuttedBy,
            Cutting = entity.Cutting,
            NsClientId = entity.NsClientId,
            First = entity.First,
            End = entity.End,
            TransmitterId = entity.TransmitterId,
            EventTime = entity.EventTime,
            IsAnnouncement = entity.IsAnnouncement,
            DataSource = entity.DataSource,
            AdditionalProperties = DeserializeJsonProperty<Dictionary<string, object>>(
                entity.AdditionalPropertiesJson
            ),
            SrvModified = entity.SysUpdatedAt != default
                ? new DateTimeOffset(entity.SysUpdatedAt, TimeSpan.Zero).ToUnixTimeMilliseconds()
                : null,
            SrvCreated = entity.SysCreatedAt != default
                ? new DateTimeOffset(entity.SysCreatedAt, TimeSpan.Zero).ToUnixTimeMilliseconds()
                : null,

            // GlucoseData
            Glucose = entity.GlucoseData.Glucose,
            GlucoseType = entity.GlucoseData.GlucoseType,
            Mgdl = entity.GlucoseData.Mgdl,
            Mmol = entity.GlucoseData.Mmol,
            Units = entity.GlucoseData.Units,

            // Nutritional
            Protein = entity.Nutritional.Protein,
            Fat = entity.Nutritional.Fat,
            FoodType = entity.Nutritional.FoodType,
            CarbTime = entity.Nutritional.CarbTime,
            AbsorptionTime = entity.Nutritional.AbsorptionTime,

            // Basal
            Rate = entity.Basal.Rate,
            Percent = entity.Basal.Percent,
            Absolute = entity.Basal.Absolute,
            Relative = entity.Basal.Relative,
            DurationType = entity.Basal.DurationType,
            EndMills = entity.Basal.EndMills,
            DurationInMilliseconds = entity.Basal.DurationInMilliseconds,

            // BolusCalc
            InsulinRecommendationForCarbs = entity.BolusCalc.InsulinRecommendationForCarbs,
            InsulinRecommendationForCorrection = entity.BolusCalc.InsulinRecommendationForCorrection,
            InsulinProgrammed = entity.BolusCalc.InsulinProgrammed,
            InsulinDelivered = entity.BolusCalc.InsulinDelivered,
            InsulinOnBoard = entity.BolusCalc.InsulinOnBoard,
            BloodGlucoseInput = entity.BolusCalc.BloodGlucoseInput,
            BloodGlucoseInputSource = entity.BolusCalc.BloodGlucoseInputSource,
            CalculationType = Enum.TryParse<CalculationType>(entity.BolusCalc.CalculationType, out var calcType) ? calcType : null,
            BolusCalc = DeserializeJsonProperty<Dictionary<string, object>>(entity.BolusCalc.BolusCalcJson),
            BolusCalculatorResult = entity.BolusCalc.BolusCalculatorResult,
            EnteredInsulin = entity.BolusCalc.EnteredInsulin,
            SplitNow = entity.BolusCalc.SplitNow,
            SplitExt = entity.BolusCalc.SplitExt,
            CR = entity.BolusCalc.CR,
            PreBolus = entity.BolusCalc.PreBolus,

            // ProfileData
            Profile = entity.ProfileData.Profile,
            ProfileJson = entity.ProfileData.ProfileJson,
            EndProfile = entity.ProfileData.EndProfile,
            CircadianPercentageProfile = entity.ProfileData.CircadianPercentageProfile,
            Percentage = entity.ProfileData.Percentage,
            Timeshift = entity.ProfileData.Timeshift,
            InsulinNeedsScaleFactor = entity.ProfileData.InsulinNeedsScaleFactor,

            // Aaps
            PumpId = entity.Aaps.PumpId,
            PumpSerial = entity.Aaps.PumpSerial,
            PumpType = entity.Aaps.PumpType,
            EndId = entity.Aaps.EndId,
            IsValid = entity.Aaps.IsValid,
            IsReadOnly = entity.Aaps.IsReadOnly,
            IsBasalInsulin = entity.Aaps.IsBasalInsulin,
            OriginalDuration = entity.Aaps.OriginalDuration,
            OriginalProfileName = entity.Aaps.OriginalProfileName,
            OriginalPercentage = entity.Aaps.OriginalPercentage,
            OriginalTimeshift = entity.Aaps.OriginalTimeshift,
            OriginalCustomizedName = entity.Aaps.OriginalCustomizedName,
            OriginalEnd = entity.Aaps.OriginalEnd,

            // Loop
            RemoteCarbs = entity.Loop.RemoteCarbs,
            RemoteAbsorption = entity.Loop.RemoteAbsorption,
            RemoteBolus = entity.Loop.RemoteBolus,
            Otp = entity.Loop.Otp,
            ReasonDisplay = entity.Loop.ReasonDisplay,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(TreatmentEntity entity, Treatment treatment)
    {
        // Root fields
        entity.EventType = treatment.EventType;
        entity.Reason = treatment.Reason;
        entity.Carbs = treatment.Carbs;
        entity.Insulin = treatment.Insulin;
        entity.Duration = treatment.Duration;
        entity.Mills = treatment.Mills;
        entity.Created_at = treatment.Created_at;
        entity.Date = treatment.Date;
        entity.Timestamp = ParseTimestampToLong(treatment.Timestamp);
        entity.UtcOffset = treatment.UtcOffset;
        entity.Notes = treatment.Notes;
        entity.EnteredBy = treatment.EnteredBy;
        entity.Status = treatment.Status;
        entity.TargetTop = treatment.TargetTop;
        entity.TargetBottom = treatment.TargetBottom;
        entity.Split = treatment.Split;
        entity.CuttedBy = treatment.CuttedBy;
        entity.Cutting = treatment.Cutting;
        entity.NsClientId = treatment.NsClientId;
        entity.First = treatment.First;
        entity.End = treatment.End;
        entity.TransmitterId = treatment.TransmitterId;
        entity.EventTime = treatment.EventTime;
        entity.IsAnnouncement = treatment.IsAnnouncement;
        entity.DataSource = treatment.DataSource;
        entity.AdditionalPropertiesJson =
            treatment.AdditionalProperties != null
                ? JsonSerializer.Serialize(treatment.AdditionalProperties)
                : null;

        // GlucoseData
        entity.GlucoseData.Glucose = treatment.Glucose;
        entity.GlucoseData.GlucoseType = treatment.GlucoseType;
        entity.GlucoseData.Mgdl = treatment.Mgdl;
        entity.GlucoseData.Mmol = treatment.Mmol;
        entity.GlucoseData.Units = treatment.Units;

        // Nutritional
        entity.Nutritional.Protein = treatment.Protein;
        entity.Nutritional.Fat = treatment.Fat;
        entity.Nutritional.FoodType = treatment.FoodType;
        entity.Nutritional.CarbTime = treatment.CarbTime;
        entity.Nutritional.AbsorptionTime = treatment.AbsorptionTime;

        // Basal
        entity.Basal.Rate = treatment.Rate;
        entity.Basal.Percent = treatment.Percent;
        entity.Basal.Absolute = treatment.Absolute;
        entity.Basal.Relative = treatment.Relative;
        entity.Basal.DurationType = treatment.DurationType;
        entity.Basal.EndMills = treatment.EndMills;
        entity.Basal.DurationInMilliseconds = treatment.DurationInMilliseconds;

        // BolusCalc
        entity.BolusCalc.InsulinRecommendationForCarbs = treatment.InsulinRecommendationForCarbs;
        entity.BolusCalc.InsulinRecommendationForCorrection = treatment.InsulinRecommendationForCorrection;
        entity.BolusCalc.InsulinProgrammed = treatment.InsulinProgrammed;
        entity.BolusCalc.InsulinDelivered = treatment.InsulinDelivered;
        entity.BolusCalc.InsulinOnBoard = treatment.InsulinOnBoard;
        entity.BolusCalc.BloodGlucoseInput = treatment.BloodGlucoseInput;
        entity.BolusCalc.BloodGlucoseInputSource = treatment.BloodGlucoseInputSource;
        entity.BolusCalc.CalculationType = treatment.CalculationType?.ToString();
        entity.BolusCalc.BolusCalcJson =
            treatment.BolusCalc != null ? JsonSerializer.Serialize(treatment.BolusCalc) : null;
        entity.BolusCalc.BolusCalculatorResult = treatment.BolusCalculatorResult;
        entity.BolusCalc.EnteredInsulin = treatment.EnteredInsulin;
        entity.BolusCalc.SplitNow = treatment.SplitNow;
        entity.BolusCalc.SplitExt = treatment.SplitExt;
        entity.BolusCalc.CR = treatment.CR;
        entity.BolusCalc.PreBolus = treatment.PreBolus;

        // ProfileData
        entity.ProfileData.Profile = treatment.Profile;
        entity.ProfileData.ProfileJson = treatment.ProfileJson;
        entity.ProfileData.EndProfile = treatment.EndProfile;
        entity.ProfileData.CircadianPercentageProfile = treatment.CircadianPercentageProfile;
        entity.ProfileData.Percentage = treatment.Percentage;
        entity.ProfileData.Timeshift = treatment.Timeshift;
        entity.ProfileData.InsulinNeedsScaleFactor = treatment.InsulinNeedsScaleFactor;

        // Aaps
        entity.Aaps.PumpId = treatment.PumpId;
        entity.Aaps.PumpSerial = treatment.PumpSerial;
        entity.Aaps.PumpType = treatment.PumpType;
        entity.Aaps.EndId = treatment.EndId;
        entity.Aaps.IsValid = treatment.IsValid;
        entity.Aaps.IsReadOnly = treatment.IsReadOnly;
        entity.Aaps.IsBasalInsulin = treatment.IsBasalInsulin;
        entity.Aaps.OriginalDuration = treatment.OriginalDuration;
        entity.Aaps.OriginalProfileName = treatment.OriginalProfileName;
        entity.Aaps.OriginalPercentage = treatment.OriginalPercentage;
        entity.Aaps.OriginalTimeshift = treatment.OriginalTimeshift;
        entity.Aaps.OriginalCustomizedName = treatment.OriginalCustomizedName;
        entity.Aaps.OriginalEnd = treatment.OriginalEnd;

        // Loop
        entity.Loop.RemoteCarbs = treatment.RemoteCarbs;
        entity.Loop.RemoteAbsorption = treatment.RemoteAbsorption;
        entity.Loop.RemoteBolus = treatment.RemoteBolus;
        entity.Loop.Otp = treatment.Otp;
        entity.Loop.ReasonDisplay = treatment.ReasonDisplay;
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
