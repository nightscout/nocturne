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

    /// <summary>
    /// When an API secret the caller saves on this connector stops working: the end of the caller's
    /// own access, since the secret is adopted as a device credential they made. Null for a connector
    /// that adopts no secret, and for a caller whose access does not end.
    /// </summary>
    [JsonPropertyName("savedSecretStopsAt")]
    public DateTime? SavedSecretStopsAt { get; set; }
}
