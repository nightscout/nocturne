namespace Nocturne.Connectors.FreeStyle.Models;

public class LibreGlucoseMeasurement
{
    public required string FactoryTimestamp { get; set; }
    public int ValueInMgPerDl { get; set; }
    public int TrendArrow { get; set; }
}