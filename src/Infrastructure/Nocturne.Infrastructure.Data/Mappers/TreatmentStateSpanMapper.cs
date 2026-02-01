using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between Treatment and StateSpan for basal delivery compatibility.
/// Enables v1-v3 API compatibility during migration from Treatment to StateSpan storage.
/// </summary>
public static class TreatmentStateSpanMapper
{
    /// <summary>
    /// Event types that indicate a temp basal treatment (case-insensitive)
    /// </summary>
    private static readonly string[] TempBasalEventTypes =
    [
        "Temp Basal",
        "Temp Basal Start",
        "TempBasal"
    ];

    /// <summary>
    /// Determines if a treatment is a temp basal treatment
    /// </summary>
    /// <param name="treatment">The treatment to check</param>
    /// <returns>True if the treatment is a temp basal, false otherwise</returns>
    public static bool IsTempBasalTreatment(Treatment treatment)
    {
        if (treatment == null || string.IsNullOrEmpty(treatment.EventType))
            return false;

        return TempBasalEventTypes.Any(
            eventType => string.Equals(treatment.EventType, eventType, StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    /// Converts a temp basal Treatment to a BasalDelivery StateSpan
    /// </summary>
    /// <param name="treatment">The treatment to convert</param>
    /// <returns>A StateSpan representation of the basal delivery, or null if not a temp basal</returns>
    public static StateSpan? ToBasalDeliveryStateSpan(Treatment treatment)
    {
        if (treatment == null || !IsTempBasalTreatment(treatment))
            return null;

        var stateSpan = new StateSpan
        {
            Category = StateSpanCategory.BasalDelivery,
            State = BasalDeliveryState.Active.ToString(),
            StartMills = treatment.Mills,
            EndMills = CalculateEndMills(treatment),
            Source = treatment.DataSource ?? treatment.EnteredBy ?? "nightscout",
            OriginalId = treatment.Id,
            Metadata = BuildBasalDeliveryMetadata(treatment)
        };

        return stateSpan;
    }

    /// <summary>
    /// Converts a BasalDelivery StateSpan back to a Treatment
    /// </summary>
    /// <param name="stateSpan">The StateSpan to convert</param>
    /// <returns>A Treatment representation of the StateSpan, or null if not a BasalDelivery category</returns>
    public static Treatment? ToTreatment(StateSpan stateSpan)
    {
        if (stateSpan == null || stateSpan.Category != StateSpanCategory.BasalDelivery)
            return null;

        // Compute Created_at from Mills
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(stateSpan.StartMills)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var treatment = new Treatment
        {
            Id = stateSpan.OriginalId ?? stateSpan.Id,
            EventType = "Temp Basal",
            Mills = stateSpan.StartMills,
            Created_at = createdAt,
            EndMills = stateSpan.EndMills,
            Duration = CalculateDuration(stateSpan),
            DataSource = stateSpan.Source,
            UtcOffset = 0 // Default to UTC, matching Nightscout behavior
        };

        // Extract metadata values
        if (stateSpan.Metadata != null)
        {
            treatment.Rate = GetMetadataDouble(stateSpan.Metadata, "rate");
            treatment.Absolute = GetMetadataDouble(stateSpan.Metadata, "absolute");
            treatment.Percent = GetMetadataDouble(stateSpan.Metadata, "percent");
            treatment.Temp = GetMetadataString(stateSpan.Metadata, "temp");
            treatment.EnteredBy = GetMetadataString(stateSpan.Metadata, "enteredBy");
            treatment.UtcOffset = GetMetadataInt(stateSpan.Metadata, "utcOffset") ?? 0;
        }

        return treatment;
    }

    /// <summary>
    /// Calculates the end time in milliseconds based on duration
    /// </summary>
    private static long? CalculateEndMills(Treatment treatment)
    {
        if (treatment.Duration.HasValue && treatment.Duration.Value > 0)
        {
            // Duration is in minutes, convert to milliseconds
            return treatment.Mills + (long)(treatment.Duration.Value * 60 * 1000);
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
    /// Builds the metadata dictionary from treatment properties for BasalDelivery
    /// </summary>
    private static Dictionary<string, object>? BuildBasalDeliveryMetadata(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>();

        if (treatment.Rate.HasValue)
            metadata["rate"] = treatment.Rate.Value;

        if (treatment.Absolute.HasValue)
            metadata["absolute"] = treatment.Absolute.Value;

        if (treatment.Percent.HasValue)
            metadata["percent"] = treatment.Percent.Value;

        if (!string.IsNullOrEmpty(treatment.Temp))
            metadata["temp"] = treatment.Temp;

        if (!string.IsNullOrEmpty(treatment.EnteredBy))
            metadata["enteredBy"] = treatment.EnteredBy;

        // Store utcOffset for restoration
        metadata["utcOffset"] = treatment.UtcOffset ?? 0;

        // Mark origin as Manual (user-initiated temp basal from v1-v3 API)
        metadata["origin"] = BasalDeliveryOrigin.Manual.ToString();

        return metadata.Count > 0 ? metadata : null;
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
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => null
        };
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
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String => je.GetString(),
            _ => value?.ToString()
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
            float f => (int)f,
            decimal dec => (int)dec,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetInt32(),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }
}
