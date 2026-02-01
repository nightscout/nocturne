using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting BasalDelivery StateSpans to Treatment objects for V1-V3 API compatibility.
/// All origins map to "Temp Basal" event type since V1-V3 doesn't have a distinct "Basal" event type.
/// </summary>
public static class BasalDeliveryStateSpanToTreatmentMapper
{
    /// <summary>
    /// Converts a BasalDelivery StateSpan to a Treatment for V1-V3 API compatibility.
    /// </summary>
    /// <param name="stateSpan">The BasalDelivery StateSpan to convert</param>
    /// <returns>A Treatment representation of the BasalDelivery, or null if not a BasalDelivery category</returns>
    public static Treatment? ToTreatment(StateSpan stateSpan)
    {
        if (stateSpan == null || stateSpan.Category != StateSpanCategory.BasalDelivery)
            return null;

        // Compute Created_at from StartMills
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(stateSpan.StartMills)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        // Calculate duration in minutes
        double? duration = null;
        if (stateSpan.EndMills.HasValue)
        {
            duration = (stateSpan.EndMills.Value - stateSpan.StartMills) / 60000.0;
        }

        // Get rate from metadata
        var rate = GetMetadataDouble(stateSpan.Metadata, "rate");

        // For suspended origin, rate should be 0
        var origin = GetMetadataString(stateSpan.Metadata, "origin");
        if (string.Equals(origin, "suspended", StringComparison.OrdinalIgnoreCase))
        {
            rate = 0;
        }

        var treatment = new Treatment
        {
            // Use OriginalId for the treatment ID to maintain compatibility
            Id = stateSpan.OriginalId ?? stateSpan.Id,
            // All origins map to "Temp Basal" since V1-V3 doesn't have a Basal event type
            EventType = "Temp Basal",
            Mills = stateSpan.StartMills,
            Created_at = createdAt,
            EndMills = stateSpan.EndMills,
            Duration = duration,
            Rate = rate,
            Absolute = rate, // Same as rate for absolute temp basals
            EnteredBy = stateSpan.Source,
            DataSource = stateSpan.Source,
            UtcOffset = GetMetadataInt(stateSpan.Metadata, "utcOffset") ?? 0,
            Temp = "absolute" // BasalDelivery uses absolute rates
        };

        // Carry over additional metadata if present
        if (stateSpan.Metadata != null)
        {
            var scheduledRate = GetMetadataDouble(stateSpan.Metadata, "scheduledRate");
            if (scheduledRate.HasValue)
            {
                treatment.AdditionalProperties ??= new Dictionary<string, object>();
                treatment.AdditionalProperties["scheduledRate"] = scheduledRate.Value;
            }

            // Preserve origin in additional properties for debugging/tracing
            if (!string.IsNullOrEmpty(origin))
            {
                treatment.AdditionalProperties ??= new Dictionary<string, object>();
                treatment.AdditionalProperties["basalOrigin"] = origin;
            }
        }

        return treatment;
    }

    /// <summary>
    /// Converts multiple BasalDelivery StateSpans to Treatments.
    /// </summary>
    /// <param name="stateSpans">The StateSpans to convert</param>
    /// <returns>Collection of Treatment objects</returns>
    public static IEnumerable<Treatment> ToTreatments(IEnumerable<StateSpan> stateSpans)
    {
        return stateSpans
            .Where(s => s.Category == StateSpanCategory.BasalDelivery)
            .Select(ToTreatment)
            .Where(t => t != null)
            .Cast<Treatment>();
    }

    /// <summary>
    /// Safely extracts a double value from metadata
    /// </summary>
    private static double? GetMetadataDouble(Dictionary<string, object>? metadata, string key)
    {
        if (metadata == null || !metadata.TryGetValue(key, out var value))
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
    private static string? GetMetadataString(Dictionary<string, object>? metadata, string key)
    {
        if (metadata == null || !metadata.TryGetValue(key, out var value))
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
    private static int? GetMetadataInt(Dictionary<string, object>? metadata, string key)
    {
        if (metadata == null || !metadata.TryGetValue(key, out var value))
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
