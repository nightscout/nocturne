using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Models;

public class GlookoBatchData
{
    [JsonPropertyName("foods")] public GlookoFood[]? Foods { get; set; }

    [JsonPropertyName("scheduledBasals")] public GlookoBasal[]? ScheduledBasals { get; set; }

    [JsonPropertyName("normalBoluses")] public GlookoBolus[]? NormalBoluses { get; set; }

    [JsonPropertyName("readings")] public GlookoCgmReading[]? Readings { get; set; }

    [JsonPropertyName("suspendBasals")] public GlookoSuspendBasal[]? SuspendBasals { get; set; }

    [JsonPropertyName("temporaryBasals")] public GlookoTempBasal[]? TempBasals { get; set; }
}

public class GlookoFood
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("pumpTimestamp")] public string PumpTimestamp { get; set; } = string.Empty;

    [JsonPropertyName("carbs")] public double Carbs { get; set; }

    [JsonPropertyName("carbohydrateGrams")]
    public double CarbohydrateGrams { get; set; }

    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
}

public class GlookoBasal
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("pumpTimestamp")] public string PumpTimestamp { get; set; } = string.Empty;

    [JsonPropertyName("rate")] public double Rate { get; set; }

    [JsonPropertyName("duration")] public int Duration { get; set; }

    [JsonPropertyName("startTime")] public int? StartTime { get; set; }

    [JsonPropertyName("pumpTimestampUtcOffset")]
    public string? PumpTimestampUtcOffset { get; set; }
}

public class GlookoBolus
{
    [JsonPropertyName("pumpTimestamp")] public string PumpTimestamp { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("insulinDelivered")] public double InsulinDelivered { get; set; }

    [JsonPropertyName("carbsInput")] public double CarbsInput { get; set; }

    [JsonPropertyName("deliveredUnits")] public double DeliveredUnits { get; set; }

    [JsonPropertyName("programmedUnits")] public double ProgrammedUnits { get; set; }
}

public class GlookoCgmReading
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("value")] public double Value { get; set; }

    [JsonPropertyName("trend")] public string? Trend { get; set; }
}

public class GlookoSuspendBasal
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("duration")] public int Duration { get; set; }

    [JsonPropertyName("suspendReason")] public string? SuspendReason { get; set; }
}

public class GlookoTempBasal
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("rate")] public double Rate { get; set; }

    [JsonPropertyName("duration")] public int Duration { get; set; }

    [JsonPropertyName("percent")] public int? Percent { get; set; }

    [JsonPropertyName("tempBasalType")] public string? TempBasalType { get; set; }
}