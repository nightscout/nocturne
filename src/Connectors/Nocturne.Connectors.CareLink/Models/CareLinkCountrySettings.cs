using System.Text.Json.Serialization;

namespace Nocturne.Connectors.CareLink.Models;

public class CareLinkCountrySettings
{
    [JsonPropertyName("blePereodicDataEndpoint")]
    public string? BlePeriodicDataEndpoint { get; set; }
}
