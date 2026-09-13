namespace Nocturne.Core.Models.Configuration;

public class YearOverviewColorPreferences
{
    public double[]? AvgGlucose { get; set; }
    public double[]? AvgGlucoseBand { get; set; }
    public double[]? Tir { get; set; }
    public double[]? Bolus { get; set; }
    public double[]? Basal { get; set; }
    public double[]? Tdd { get; set; }
    public double[]? Carbs { get; set; }

    public string? Validate()
    {
        foreach (var (field, values) in new[]
        {
            ("avgGlucose", AvgGlucose), ("avgGlucoseBand", AvgGlucoseBand), ("tir", Tir),
            ("bolus", Bolus), ("basal", Basal), ("tdd", Tdd), ("carbs", Carbs),
        })
        {
            if (values is null) continue;
            var is4 = field == "avgGlucose";
            if (values.Length != (is4 ? 4 : 2)
                || values.Any(value => !double.IsFinite(value))
                || values.Zip(values.Skip(1)).Any(pair => pair.First >= pair.Second)
                || (field == "avgGlucose" ? values[0] <= 40 || values[^1] >= 350 : (field == "avgGlucoseBand" ? values[0] < 40 || values[^1] > 350 : values[0] < 0))
                || (field == "tir" && values[^1] > 100))
            {
                return $"yearOverviewColors.{field}: invalid_color_range";
            }
        }
        return null;
    }
}
