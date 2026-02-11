namespace Nocturne.Desktop.Tray.Helpers;

/// <summary>
/// Maps Nightscout direction strings to Segoe Fluent Icons arrow glyphs.
/// </summary>
public static class TrendHelper
{
    /// <summary>
    /// Returns a Segoe Fluent Icons glyph string for the given direction.
    /// </summary>
    public static string GetArrowGlyph(string? direction)
    {
        return direction switch
        {
            "TripleUp" => "\uE74A",      // ChevronUp (double used for triple)
            "DoubleUp" => "\uE74A",      // ChevronUp
            "SingleUp" => "\uE74A",      // ChevronUp
            "FortyFiveUp" => "\uE76C",   // ChevronRight rotated conceptually - using UpRight
            "Flat" => "\uE76C",          // ChevronRight (→)
            "FortyFiveDown" => "\uE76C", // DownRight
            "SingleDown" => "\uE74B",    // ChevronDown
            "DoubleDown" => "\uE74B",    // ChevronDown
            "TripleDown" => "\uE74B",    // ChevronDown
            _ => "\uE9CE",              // Help/unknown
        };
    }

    /// <summary>
    /// Returns the rotation angle in degrees for the arrow glyph to represent the correct direction.
    /// This allows using a single up-arrow glyph (\uE74A) rotated appropriately.
    /// </summary>
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

    /// <summary>
    /// Returns a simple Unicode arrow character for use in the tray icon tooltip.
    /// </summary>
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
