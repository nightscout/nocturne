using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>Clinical severity of a nocturnal hypoglycemic event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SleepHypoSeverity>))]
public enum SleepHypoSeverity
{
    /// <summary>Glucose dropped below 70 mg/dL (hypoglycemia onset).</summary>
    Low,

    /// <summary>Glucose dropped below 54 mg/dL (severe hypoglycemia).</summary>
    VeryLow,
}
