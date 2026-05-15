using System.Text.Json.Serialization;
using Nocturne.Connectors.Dexcom.Converters;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Dexcom.Models;

public class DexcomEntry
{
    public string Dt { get; set; } = string.Empty;
    public string St { get; set; } = string.Empty;

    [JsonConverter(typeof(DexcomTrendConverter))]
    public GlucoseDirection Trend { get; set; }

    public int Value { get; set; }
    public string Wt { get; set; } = string.Empty;
}
