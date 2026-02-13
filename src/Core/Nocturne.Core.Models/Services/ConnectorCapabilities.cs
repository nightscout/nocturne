using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Services;

public class ConnectorCapabilities
{
    [JsonPropertyName("supportedDataTypes")]
    public List<string> SupportedDataTypes { get; set; } = [];

    [JsonPropertyName("supportsHistoricalSync")]
    public bool SupportsHistoricalSync { get; set; }

    [JsonPropertyName("maxHistoricalDays")]
    public int? MaxHistoricalDays { get; set; }

    [JsonPropertyName("supportsManualSync")]
    public bool SupportsManualSync { get; set; }
}
