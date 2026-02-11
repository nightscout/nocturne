using Nocturne.Widget.Contracts.Helpers;

namespace Nocturne.Desktop.Tray.Helpers;

/// <summary>
/// Maps Nightscout direction strings to Segoe Fluent Icons arrow glyphs.
/// Windows-specific rendering methods live here; platform-independent arrow text
/// and direction labels are delegated to <see cref="DirectionHelper"/>.
/// </summary>
public static class TrendHelper
{
    /// <summary>
    /// Returns a Segoe Fluent Icons glyph for the given direction.
    /// Windows-specific: uses Segoe Fluent Icon font codepoints.
    /// </summary>
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

    /// <summary>
    /// Returns the rotation angle for a Segoe Fluent Icons arrow glyph.
    /// Windows-specific: used with WinUI RotateTransform.
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
    /// Returns a Unicode arrow character for the given direction.
    /// Delegates to <see cref="DirectionHelper.GetArrowText"/>.
    /// </summary>
    public static string GetArrowText(string? direction) =>
        DirectionHelper.GetArrowText(direction);

    /// <summary>
    /// Returns a human-readable label for the given direction (e.g. "Rising slowly").
    /// Delegates to <see cref="DirectionHelper.GetDirectionLabel"/>.
    /// </summary>
    public static string GetDirectionLabel(string? direction) =>
        DirectionHelper.GetDirectionLabel(direction);
}
