using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Tidepool.Models;

public class TidepoolPumpSettings
{
    [JsonPropertyName("activeSchedule")]
    public string? ActiveSchedule { get; set; }

    [JsonPropertyName("automatedDelivery")]
    public bool AutomatedDelivery { get; set; }

    [JsonPropertyName("deviceTime")]
    public DateTime? DeviceTime { get; set; }

    [JsonPropertyName("basalSchedules")]
    public Dictionary<string, List<TidepoolBasalSchedule>>? BasalSchedules { get; set; }

    [JsonPropertyName("bgTargets")]
    public Dictionary<string, List<TidepoolGlucoseTarget>>? BgTargets { get; set; }

    [JsonPropertyName("carbRatios")]
    public Dictionary<string, List<TidepoolCarbRatio>>? CarbRatios { get; set; }

    [JsonPropertyName("insulinSensitivities")]
    public Dictionary<string, List<TidepoolInsulinSensitivity>>? InsulinSensitivities { get; set; }

    [JsonPropertyName("units")]
    public TidepoolUnits? Units { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("time")]
    public DateTime? Time { get; set; }
}

public class TidepoolBasalSchedule
{
    [JsonPropertyName("rate")]
    public double Rate { get; set; }

    [JsonPropertyName("start")]
    public int Start { get; set; }
}

public class TidepoolGlucoseTarget
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("target")]
    public double Target { get; set; }
}

public class TidepoolCarbRatio
{
    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    [JsonPropertyName("start")]
    public int Start { get; set; }
}

public class TidepoolInsulinSensitivity
{
    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    [JsonPropertyName("start")]
    public int Start { get; set; }
}

public class TidepoolUnits
{
    [JsonPropertyName("bg")]
    public string? Bg { get; set; }

    [JsonPropertyName("carb")]
    public string? Carb { get; set; }
}
