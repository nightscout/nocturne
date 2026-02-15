using MongoDB.Bson;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Tools.Migration.Services.Transformers;

/// <summary>
/// Transformer for Treatment documents
/// Handles complex BolusCalc objects, duration processing, and multiple treatment types
/// </summary>
public class TreatmentTransformer : BaseDocumentTransformer
{
    public TreatmentTransformer(TransformationOptions? options = null)
        : base("treatments", options) { }

    public override async Task<object> TransformAsync(BsonDocument document)
    {
        try
        {
            var entity = new TreatmentEntity();

            // Transform ID
            var originalId = document.GetValue("_id", BsonNull.Value);
            entity.OriginalId = ToString(originalId, 24);
            entity.Id = _options.GenerateNewUuids
                ? ConvertObjectIdToGuid(entity.OriginalId)
                : Guid.CreateVersion7();

            // Transform timestamps
            await TransformTimestamps(document, entity);

            // Transform basic treatment fields
            entity.EventType = ToString(document.GetValue("eventType", BsonNull.Value), 255);
            entity.Reason = ToString(document.GetValue("reason", BsonNull.Value));

            // Transform glucose information
            entity.GlucoseData.Glucose = ToNullableDouble(document.GetValue("glucose", BsonNull.Value));
            entity.GlucoseData.GlucoseType = ToString(document.GetValue("glucoseType", BsonNull.Value), 50);
            entity.GlucoseData.Units = ToString(document.GetValue("units", BsonNull.Value), 10);

            // Transform medication data
            entity.Insulin = ToNullableDouble(document.GetValue("insulin", BsonNull.Value));
            entity.Carbs = ToNullableDouble(document.GetValue("carbs", BsonNull.Value));

            // Transform basal information
            entity.Duration = ToNullableInt32(document.GetValue("duration", BsonNull.Value));
            entity.Basal.Percent = ToNullableDouble(document.GetValue("percent", BsonNull.Value));
            entity.Basal.Absolute = ToNullableDouble(document.GetValue("absolute", BsonNull.Value));

            // Transform text fields
            entity.Notes = ToString(document.GetValue("notes", BsonNull.Value));
            entity.EnteredBy = ToString(document.GetValue("enteredBy", BsonNull.Value), 255);

            // Transform complex BolusCalc object to JSONB
            await TransformBolusCalc(document, entity);

            // Transform additional properties to JSONB
            await TransformAdditionalProperties(document, entity);

            // Set system tracking timestamps
            entity.SysCreatedAt = DateTime.UtcNow;
            entity.SysUpdatedAt = DateTime.UtcNow;

            // Update statistics
            RecordTransformationSuccess();

            return entity;
        }
        catch (Exception ex)
        {
            RecordTransformationFailure(ex.Message);
            throw new InvalidOperationException(
                $"Failed to transform treatment document: {ex.Message}",
                ex
            );
        }
    }

    public override async Task<TransformationValidationResult> ValidateAsync(BsonDocument document)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var suggestedFixes = new List<string>();

        await Task.CompletedTask; // Make async

        // Validate essential fields
        if (!document.Contains("_id"))
        {
            errors.Add("Document is missing required _id field");
            suggestedFixes.Add("Ensure all treatment documents have a valid ObjectId");
        }

        // Validate event type
        if (!document.Contains("eventType") || document["eventType"] == BsonNull.Value)
        {
            warnings.Add("Treatment is missing eventType field");
            suggestedFixes.Add("Specify eventType for better treatment categorization");
        }

        // Validate timestamp fields
        var hasCreatedAt =
            document.Contains("created_at") && document["created_at"] != BsonNull.Value;
        var hasDate = document.Contains("date") && document["date"] != BsonNull.Value;
        var hasMills = document.Contains("mills") && document["mills"] != BsonNull.Value;

        if (!hasCreatedAt && !hasDate && !hasMills)
        {
            errors.Add("No valid timestamp found (created_at, date, or mills)");
            suggestedFixes.Add("Ensure treatment documents contain valid timestamp information");
        }

        // Validate treatment-specific fields
        await ValidateTreatmentSpecificFields(document, errors, warnings, suggestedFixes);

        // Validate BolusCalc if present
        if (document.Contains("boluscalc") && document["boluscalc"] != BsonNull.Value)
        {
            await ValidateBolusCalc(document["boluscalc"], errors, warnings, suggestedFixes);
        }

        return new TransformationValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
            SuggestedFixes = suggestedFixes,
        };
    }

    private async Task TransformTimestamps(BsonDocument document, TreatmentEntity entity)
    {
        await Task.CompletedTask; // Make async

        // Priority order: created_at, mills, date
        var createdAt = document.GetValue("created_at", BsonNull.Value);
        var mills = document.GetValue("mills", BsonNull.Value);
        var date = document.GetValue("date", BsonNull.Value);
        var dateString = document.GetValue("dateString", BsonNull.Value);

        if (createdAt != BsonNull.Value)
        {
            entity.Created_at = ConvertToDateTimeString(createdAt);
            var dateTime = ConvertToDateTime(createdAt);
            entity.Mills = ((DateTimeOffset)dateTime).ToUnixTimeMilliseconds();
            UpdateFieldStatistics("created_at", createdAt, true);
        }
        else if (mills != BsonNull.Value && mills.IsInt64)
        {
            entity.Mills = mills.AsInt64;
            entity.Created_at = DateTimeOffset
                .FromUnixTimeMilliseconds(entity.Mills)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            UpdateFieldStatistics("mills", mills, true);
        }
        else if (date != BsonNull.Value)
        {
            entity.Created_at = ConvertToDateTimeString(date);
            var dateTime = ConvertToDateTime(date);
            entity.Mills = ((DateTimeOffset)dateTime).ToUnixTimeMilliseconds();
            UpdateFieldStatistics("date", date, true);
        }
        else
        {
            // Default to current time
            var now = DateTime.UtcNow;
            entity.Created_at = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            entity.Mills = ((DateTimeOffset)now).ToUnixTimeMilliseconds();
            RecordTransformationWarning();
        }
    }

    private async Task TransformBolusCalc(BsonDocument document, TreatmentEntity entity)
    {
        await Task.CompletedTask; // Make async

        var bolusCalc = document.GetValue("boluscalc", BsonNull.Value);

        if (bolusCalc != BsonNull.Value && bolusCalc.IsBsonDocument)
        {
            try
            {
                // Transform the complex BolusCalc object to structured JSONB
                var bolusCalcDoc = bolusCalc.AsBsonDocument;
                var bolusCalcData = new Dictionary<string, object?>();

                // Extract key BolusCalc fields
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "carbs");
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "cob");
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "bg");
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "insulinbg");
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "insulincarbs");
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "insulintrend");
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "insulin");
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "otherCorrection");
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "insulinsuperbolus");
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "trend");
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "iob");
                ExtractBolusCalcField(bolusCalcDoc, bolusCalcData, "activity");

                entity.BolusCalc.BolusCalcJson = ToJsonB(BsonDocument.Create(bolusCalcData));
                UpdateFieldStatistics("boluscalc", bolusCalc, true);
            }
            catch (Exception ex)
            {
                UpdateFieldStatistics("boluscalc", bolusCalc, false);
                RecordTransformationFailure($"BolusCalc transformation failed: {ex.Message}");
            }
        }
        else
        {
            UpdateMissingFieldStatistics("boluscalc");
        }
    }

    private void ExtractBolusCalcField(
        BsonDocument source,
        Dictionary<string, object?> target,
        string fieldName
    )
    {
        if (source.Contains(fieldName))
        {
            var value = source[fieldName];
            target[fieldName] = ConvertBsonValueToObject(value);
        }
    }

    private object? ConvertBsonValueToObject(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Null => null,
            BsonType.Boolean => value.AsBoolean,
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => value.AsInt64,
            BsonType.Double => value.AsDouble,
            BsonType.String => value.AsString,
            BsonType.DateTime => value.ToUniversalTime(),
            BsonType.ObjectId => value.AsObjectId.ToString(),
            BsonType.Array => value.AsBsonArray.Select(ConvertBsonValueToObject).ToArray(),
            BsonType.Document => value.AsBsonDocument.ToDictionary(
                element => element.Name,
                element => ConvertBsonValueToObject(element.Value)
            ),
            _ => value.ToString(),
        };
    }

    private async Task TransformAdditionalProperties(BsonDocument document, TreatmentEntity entity)
    {
        await Task.CompletedTask; // Make async

        // Collect additional properties that aren't part of the standard schema
        var standardFields = new HashSet<string>
        {
            "_id",
            "eventType",
            "reason",
            "glucose",
            "glucoseType",
            "units",
            "insulin",
            "carbs",
            "duration",
            "percent",
            "absolute",
            "notes",
            "enteredBy",
            "created_at",
            "date",
            "mills",
            "dateString",
            "boluscalc",
        };

        var additionalProps = new Dictionary<string, object?>();

        foreach (var element in document)
        {
            if (!standardFields.Contains(element.Name))
            {
                additionalProps[element.Name] = ConvertBsonValueToObject(element.Value);
            }
        }

        // Filter out null values if not preserving them
        var filteredProps = FilterNullProperties(additionalProps);

        if (filteredProps.Count > 0)
        {
            entity.AdditionalPropertiesJson = ToJsonB(BsonDocument.Create(filteredProps));
        }
    }

    private async Task ValidateTreatmentSpecificFields(
        BsonDocument document,
        List<string> errors,
        List<string> warnings,
        List<string> suggestedFixes
    )
    {
        await Task.CompletedTask; // Make async

        var eventType = document.GetValue("eventType", BsonNull.Value).ToString();

        // Validate specific treatment types
        switch (eventType?.ToLowerInvariant())
        {
            case "meal bolus":
            case "snack bolus":
                ValidateMealBolus(document, warnings, suggestedFixes);
                break;
            case "correction bolus":
                ValidateCorrectionBolus(document, warnings, suggestedFixes);
                break;
            case "temp basal":
            case "temp basal start":
            case "temp basal end":
                ValidateTempBasal(document, warnings, suggestedFixes);
                break;
            case "bg check":
                ValidateBgCheck(document, warnings, suggestedFixes);
                break;
        }

        // Validate numeric ranges
        var insulin = ToNullableDouble(document.GetValue("insulin", BsonNull.Value));
        if (insulin.HasValue && (insulin.Value < 0 || insulin.Value > 100))
        {
            warnings.Add($"Insulin value {insulin.Value} seems unusually high or negative");
        }

        var carbs = ToNullableDouble(document.GetValue("carbs", BsonNull.Value));
        if (carbs.HasValue && (carbs.Value < 0 || carbs.Value > 500))
        {
            warnings.Add($"Carbs value {carbs.Value} seems unusually high or negative");
        }
    }

    private void ValidateMealBolus(
        BsonDocument document,
        List<string> warnings,
        List<string> suggestedFixes
    )
    {
        var hasCarbs = document.Contains("carbs") && document["carbs"] != BsonNull.Value;
        var hasInsulin = document.Contains("insulin") && document["insulin"] != BsonNull.Value;

        if (!hasCarbs && !hasInsulin)
        {
            warnings.Add("Meal bolus treatment has no carbs or insulin information");
            suggestedFixes.Add("Include carbs and/or insulin data for meal bolus treatments");
        }
    }

    private void ValidateCorrectionBolus(
        BsonDocument document,
        List<string> warnings,
        List<string> suggestedFixes
    )
    {
        var hasInsulin = document.Contains("insulin") && document["insulin"] != BsonNull.Value;
        var hasGlucose = document.Contains("glucose") && document["glucose"] != BsonNull.Value;

        if (!hasInsulin)
        {
            warnings.Add("Correction bolus treatment has no insulin information");
            suggestedFixes.Add("Include insulin data for correction bolus treatments");
        }

        if (!hasGlucose)
        {
            warnings.Add("Correction bolus treatment has no glucose reading");
            suggestedFixes.Add("Include glucose reading for correction bolus treatments");
        }
    }

    private void ValidateTempBasal(
        BsonDocument document,
        List<string> warnings,
        List<string> suggestedFixes
    )
    {
        var hasPercent = document.Contains("percent") && document["percent"] != BsonNull.Value;
        var hasAbsolute = document.Contains("absolute") && document["absolute"] != BsonNull.Value;
        var hasDuration = document.Contains("duration") && document["duration"] != BsonNull.Value;

        if (!hasPercent && !hasAbsolute)
        {
            warnings.Add("Temp basal treatment has no percent or absolute rate");
            suggestedFixes.Add("Include percent or absolute rate for temp basal treatments");
        }

        if (!hasDuration)
        {
            warnings.Add("Temp basal treatment has no duration");
            suggestedFixes.Add("Include duration for temp basal treatments");
        }
    }

    private void ValidateBgCheck(
        BsonDocument document,
        List<string> warnings,
        List<string> suggestedFixes
    )
    {
        var hasGlucose = document.Contains("glucose") && document["glucose"] != BsonNull.Value;

        if (!hasGlucose)
        {
            warnings.Add("BG Check treatment has no glucose reading");
            suggestedFixes.Add("Include glucose reading for BG Check treatments");
        }
    }

    private async Task ValidateBolusCalc(
        BsonValue bolusCalc,
        List<string> errors,
        List<string> warnings,
        List<string> suggestedFixes
    )
    {
        await Task.CompletedTask; // Make async

        if (!bolusCalc.IsBsonDocument)
        {
            warnings.Add("BolusCalc field is not a valid document structure");
            suggestedFixes.Add("Ensure BolusCalc is properly structured as an object");
            return;
        }

        var bolusCalcDoc = bolusCalc.AsBsonDocument;

        // Validate essential BolusCalc fields
        var requiredFields = new[] { "carbs", "bg", "insulin" };

        foreach (var field in requiredFields)
        {
            if (!bolusCalcDoc.Contains(field) || bolusCalcDoc[field] == BsonNull.Value)
            {
                warnings.Add($"BolusCalc is missing {field} field");
            }
        }

        // Validate numeric ranges in BolusCalc
        if (bolusCalcDoc.Contains("bg"))
        {
            var bg = ToNullableDouble(bolusCalcDoc["bg"]);
            if (bg.HasValue && (bg.Value < 20 || bg.Value > 600))
            {
                warnings.Add($"BolusCalc BG value {bg.Value} is outside normal range");
            }
        }
    }
}
