namespace Nocturne.Core.Models.Configuration;

public class YearOverviewColorPreferences
{
    public bool? AdvancedMode { get; set; }
    public double? OutOfBandTransparency { get; set; }
    public string? LowColor { get; set; }
    public string? HighColor { get; set; }

    public double[]? AvgGlucose { get; set; }
    public double[]? AvgGlucoseBand { get; set; }
    public double[]? Tir { get; set; }
    public double[]? TirBand { get; set; }
    public string[]? TirColors { get; set; }
    public double[]? Bolus { get; set; }
    public double[]? BolusBand { get; set; }
    public string[]? BolusColors { get; set; }
    public double[]? Basal { get; set; }
    public double[]? BasalBand { get; set; }
    public string[]? BasalColors { get; set; }
    public double[]? Tdd { get; set; }
    public double[]? TddBand { get; set; }
    public string[]? TddColors { get; set; }
    public double[]? Carbs { get; set; }
    public double[]? CarbsBand { get; set; }
    public string[]? CarbsColors { get; set; }

    public string? Validate()
    {
        if (OutOfBandTransparency.HasValue && (OutOfBandTransparency.Value < 0 || OutOfBandTransparency.Value > 100))
        {
            return "yearOverviewColors.outOfBandTransparency: invalid_transparency_value";
        }

        foreach (var (field, values) in new[]
        {
            ("avgGlucose", AvgGlucose), ("avgGlucoseBand", AvgGlucoseBand),
            ("tir", Tir), ("tirBand", TirBand),
            ("bolus", Bolus), ("bolusBand", BolusBand),
            ("basal", Basal), ("basalBand", BasalBand),
            ("tdd", Tdd), ("tddBand", TddBand),
            ("carbs", Carbs), ("carbsBand", CarbsBand),
        })
        {
            if (values is null) continue;
            var is4 = field == "avgGlucose";
            if (values.Length != (is4 ? 4 : 2)
                || values.Any(value => !double.IsFinite(value))
                || values.Zip(values.Skip(1)).Any(pair => pair.First >= pair.Second)
                || (field == "avgGlucose" ? values[0] <= 40 || values[^1] >= 350 : (field == "avgGlucoseBand" ? values[0] < 40 || values[^1] > 350 : values[0] < 0))
                || ((field == "tir" || field == "tirBand") && values[^1] > 100))
            {
                return $"yearOverviewColors.{field}: invalid_color_range";
            }
        }
        return null;
    }
}
