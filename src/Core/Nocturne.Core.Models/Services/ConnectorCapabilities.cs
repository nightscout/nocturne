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
    /// that adopts no secret, for a caller whose access does not end, and for a caller who makes no
    /// credential (<see cref="SavedSecretMakesNoCredential"/>).
    /// </summary>
    [JsonPropertyName("savedSecretStopsAt")]
    public DateTime? SavedSecretStopsAt { get; set; }

    /// <summary>
    /// Whether an API secret the caller saves on this connector is kept for the connector's own use
    /// only, with no credential made from it, because the caller may not make one. False for a
    /// connector that adopts no secret.
    /// </summary>
    [JsonPropertyName("savedSecretMakesNoCredential")]
    public bool SavedSecretMakesNoCredential { get; set; }
}
