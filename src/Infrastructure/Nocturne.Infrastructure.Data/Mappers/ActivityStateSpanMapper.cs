using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between Activity and StateSpan for v1 API compatibility.
/// Activities are stored as StateSpans with categories: Exercise, Sleep, Illness, Travel.
/// </summary>
public static class ActivityStateSpanMapper
{
    /// <summary>
    /// Activity type strings that map to Exercise category
    /// </summary>
    private static readonly string[] ExerciseTypes =
    [
        "exercise",
        "running",
        "walking",
        "cycling",
        "swimming",
        "hiking",
        "gym",
        "workout",
        "sports",
        "activity"
    ];

    /// <summary>
    /// Activity type strings that map to Sleep category
    /// </summary>
    private static readonly string[] SleepTypes = ["sleep", "nap", "rest", "sleeping"];

    /// <summary>
    /// Activity type strings that map to Illness category
    /// </summary>
    private static readonly string[] IllnessTypes = ["illness", "sick", "fever", "cold", "flu", "unwell"];

    /// <summary>
    /// Activity type strings that map to Travel category
    /// </summary>
    private static readonly string[] TravelTypes = ["travel", "timezone", "flight", "trip", "vacation"];

    /// <summary>
    /// Categories that represent Activity records in StateSpan storage
    /// </summary>
    public static readonly StateSpanCategory[] ActivityCategories =
    [
        StateSpanCategory.Exercise,
        StateSpanCategory.Sleep,
        StateSpanCategory.Illness,
        StateSpanCategory.Travel
    ];

    /// <summary>
    /// Determines the StateSpanCategory for an activity type string
    /// </summary>
    /// <param name="activityType">The activity type string (e.g., "exercise", "sleep")</param>
    /// <returns>The corresponding StateSpanCategory</returns>
    public static StateSpanCategory GetCategoryForType(string? activityType)
    {
        if (string.IsNullOrEmpty(activityType))
            return StateSpanCategory.Exercise;

        var lowerType = activityType.ToLowerInvariant();

        if (SleepTypes.Any(t => lowerType.Contains(t)))
            return StateSpanCategory.Sleep;

        if (IllnessTypes.Any(t => lowerType.Contains(t)))
            return StateSpanCategory.Illness;

        if (TravelTypes.Any(t => lowerType.Contains(t)))
            return StateSpanCategory.Travel;

        // Default to Exercise for any unrecognized activity
        return StateSpanCategory.Exercise;
    }

    /// <summary>
    /// Determines if a StateSpan represents an Activity record
    /// </summary>
    /// <param name="stateSpan">The StateSpan to check</param>
    /// <returns>True if the StateSpan represents an Activity</returns>
    public static bool IsActivityStateSpan(StateSpan stateSpan)
    {
        if (stateSpan == null)
            return false;

        return ActivityCategories.Contains(stateSpan.Category);
    }

    /// <summary>
    /// Converts an Activity to a StateSpan
    /// </summary>
    /// <param name="activity">The Activity to convert</param>
    /// <returns>A StateSpan representation of the Activity</returns>
    public static StateSpan ToStateSpan(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var category = GetCategoryForType(activity.Type);

        var stateSpan = new StateSpan
        {
            Category = category,
            State = activity.Type ?? category.ToString().ToLowerInvariant(),
            StartMills = activity.Mills,
            EndMills = CalculateEndMills(activity),
            Source = activity.EnteredBy ?? "nightscout",
            OriginalId = activity.Id,
            Metadata = BuildMetadata(activity)
        };

        return stateSpan;
    }

    /// <summary>
    /// Converts a StateSpan back to an Activity
    /// </summary>
    /// <param name="stateSpan">The StateSpan to convert</param>
    /// <returns>An Activity representation of the StateSpan, or null if not an Activity category</returns>
    public static Activity? ToActivity(StateSpan stateSpan)
    {
        if (stateSpan == null || !IsActivityStateSpan(stateSpan))
            return null;

        var activity = new Activity
        {
            Id = stateSpan.OriginalId ?? stateSpan.Id,
            Mills = stateSpan.StartMills,
            Type = stateSpan.State,
            EnteredBy = stateSpan.Source,
            Duration = CalculateDuration(stateSpan)
        };

        // Extract metadata values
        if (stateSpan.Metadata != null)
        {
            activity.Description = GetMetadataString(stateSpan.Metadata, "description");
            activity.Notes = GetMetadataString(stateSpan.Metadata, "notes");
            activity.Name = GetMetadataString(stateSpan.Metadata, "name");
            activity.Intensity = GetMetadataString(stateSpan.Metadata, "intensity");
            activity.DateString = GetMetadataString(stateSpan.Metadata, "dateString");
            activity.CreatedAt = GetMetadataString(stateSpan.Metadata, "createdAt");
            activity.DistanceUnits = GetMetadataString(stateSpan.Metadata, "distanceUnits");
            activity.EnergyUnits = GetMetadataString(stateSpan.Metadata, "energyUnits");

            activity.Distance = GetMetadataDouble(stateSpan.Metadata, "distance");
            activity.Energy = GetMetadataDouble(stateSpan.Metadata, "energy");
            activity.Timestamp = GetMetadataLong(stateSpan.Metadata, "timestamp");
            activity.UtcOffset = GetMetadataInt(stateSpan.Metadata, "utcOffset");

            // Restore additional properties if present
            if (stateSpan.Metadata.TryGetValue("additionalProperties", out var additionalProps))
            {
                activity.AdditionalProperties = ExtractDictionary(additionalProps);
            }
        }

        return activity;
    }

    /// <summary>
    /// Calculates the end time in milliseconds based on duration
    /// </summary>
    private static long? CalculateEndMills(Activity activity)
    {
        if (activity.Duration.HasValue && activity.Duration.Value > 0)
        {
            // Duration is in minutes, convert to milliseconds
            return activity.Mills + (long)(activity.Duration.Value * 60 * 1000);
        }

        return null;
    }

    /// <summary>
    /// Calculates the duration in minutes from start and end mills
    /// </summary>
    private static double? CalculateDuration(StateSpan stateSpan)
    {
        if (stateSpan.EndMills.HasValue)
        {
            // Convert milliseconds to minutes
            return (stateSpan.EndMills.Value - stateSpan.StartMills) / 60000.0;
        }

        return null;
    }

    /// <summary>
    /// Builds the metadata dictionary from activity properties
    /// </summary>
    private static Dictionary<string, object>? BuildMetadata(Activity activity)
    {
        var metadata = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(activity.Description))
            metadata["description"] = activity.Description;

        if (!string.IsNullOrEmpty(activity.Notes))
            metadata["notes"] = activity.Notes;

        if (!string.IsNullOrEmpty(activity.Name))
            metadata["name"] = activity.Name;

        if (!string.IsNullOrEmpty(activity.Intensity))
            metadata["intensity"] = activity.Intensity;

        if (!string.IsNullOrEmpty(activity.DateString))
            metadata["dateString"] = activity.DateString;

        if (!string.IsNullOrEmpty(activity.CreatedAt))
            metadata["createdAt"] = activity.CreatedAt;

        if (!string.IsNullOrEmpty(activity.DistanceUnits))
            metadata["distanceUnits"] = activity.DistanceUnits;

        if (!string.IsNullOrEmpty(activity.EnergyUnits))
            metadata["energyUnits"] = activity.EnergyUnits;

        if (activity.Distance.HasValue)
            metadata["distance"] = activity.Distance.Value;

        if (activity.Energy.HasValue)
            metadata["energy"] = activity.Energy.Value;

        if (activity.Timestamp.HasValue)
            metadata["timestamp"] = activity.Timestamp.Value;

        if (activity.UtcOffset.HasValue)
            metadata["utcOffset"] = activity.UtcOffset.Value;

        if (activity.AdditionalProperties != null && activity.AdditionalProperties.Count > 0)
            metadata["additionalProperties"] = activity.AdditionalProperties;

        return metadata.Count > 0 ? metadata : null;
    }

    /// <summary>
    /// Safely extracts a string value from metadata
    /// </summary>
    private static string? GetMetadataString(Dictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            string s => s,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String =>
                je.GetString(),
            _ => value?.ToString()
        };
    }

    /// <summary>
    /// Safely extracts a double value from metadata
    /// </summary>
    private static double? GetMetadataDouble(Dictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal dec => (double)dec,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number =>
                je.GetDouble(),
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// Safely extracts a long value from metadata
    /// </summary>
    private static long? GetMetadataLong(Dictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number =>
                je.GetInt64(),
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// Safely extracts an int value from metadata
    /// </summary>
    private static int? GetMetadataInt(Dictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number =>
                je.GetInt32(),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// Extracts a dictionary from an object (handles JsonElement conversion)
    /// </summary>
    private static Dictionary<string, object>? ExtractDictionary(object value)
    {
        return value switch
        {
            Dictionary<string, object> dict => dict,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Object =>
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText()),
            _ => null
        };
    }
}
