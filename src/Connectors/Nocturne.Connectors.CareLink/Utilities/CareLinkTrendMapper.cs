using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.CareLink.Utilities;

public static class CareLinkTrendMapper
{
    private static readonly Dictionary<string, GlucoseDirection> Trends = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UP"] = GlucoseDirection.SingleUp,
        ["UP_DOUBLE"] = GlucoseDirection.DoubleUp,
        ["UP_TRIPLE"] = GlucoseDirection.DoubleUp,
        ["DOWN"] = GlucoseDirection.SingleDown,
        ["DOWN_DOUBLE"] = GlucoseDirection.DoubleDown,
        ["DOWN_TRIPLE"] = GlucoseDirection.DoubleDown,
        ["NONE"] = GlucoseDirection.None,
    };

    public static GlucoseDirection Map(string? trend)
    {
        if (string.IsNullOrEmpty(trend))
            return GlucoseDirection.None;

        return Trends.GetValueOrDefault(trend, GlucoseDirection.None);
    }
}
