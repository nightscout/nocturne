namespace Nocturne.Connectors.Dexcom.Models;

public class DexcomEntry
{
    public string Dt { get; set; } = string.Empty;
    public string St { get; set; } = string.Empty;
    public int Trend { get; set; }
    public int Value { get; set; }
    public string Wt { get; set; } = string.Empty;
}