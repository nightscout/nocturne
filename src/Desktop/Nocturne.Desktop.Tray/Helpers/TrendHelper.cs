namespace Nocturne.Desktop.Tray.Helpers;

/// <summary>
/// Maps Nightscout direction strings to Segoe Fluent Icons arrow glyphs.
/// </summary>
public static class TrendHelper
{
    public static string GetArrowGlyph(string? direction)
    {
        return direction switch
        {
            "TripleUp" => "\uE74A",
            "DoubleUp" => "\uE74A",
            "SingleUp" => "\uE74A",
            "FortyFiveUp" => "\uE76C",
            "Flat" => "\uE76C",
            "FortyFiveDown" => "\uE76C",
            "SingleDown" => "\uE74B",
            "DoubleDown" => "\uE74B",
            "TripleDown" => "\uE74B",
            _ => "\uE9CE",
        };
    }

    public static double GetArrowRotation(string? direction)
    {
        return direction switch
        {
            "TripleUp" => 0,
            "DoubleUp" => 0,
            "SingleUp" => 0,
            "FortyFiveUp" => 45,
            "Flat" => 90,
            "FortyFiveDown" => 135,
            "SingleDown" => 180,
            "DoubleDown" => 180,
            "TripleDown" => 180,
            _ => 90,
        };
    }

    public static string GetArrowText(string? direction)
    {
        return direction switch
        {
            "TripleUp" => "\u2191\u2191",
            "DoubleUp" => "\u2191\u2191",
            "SingleUp" => "\u2191",
            "FortyFiveUp" => "\u2197",
            "Flat" => "\u2192",
            "FortyFiveDown" => "\u2198",
            "SingleDown" => "\u2193",
            "DoubleDown" => "\u2193\u2193",
            "TripleDown" => "\u2193\u2193",
            _ => "?",
        };
    }

    public static string GetDirectionLabel(string? direction)
    {
        return direction switch
        {
            "TripleUp" => "Rising very rapidly",
            "DoubleUp" => "Rising rapidly",
            "SingleUp" => "Rising",
            "FortyFiveUp" => "Rising slowly",
            "Flat" => "Stable",
            "FortyFiveDown" => "Falling slowly",
            "SingleDown" => "Falling",
            "DoubleDown" => "Falling rapidly",
            "TripleDown" => "Falling very rapidly",
            "NOT COMPUTABLE" => "Not computable",
            "RATE OUT OF RANGE" => "Rate out of range",
            _ => "Unknown",
        };
    }
}
