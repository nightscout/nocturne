using Nocturne.Widget.Contracts.Helpers;

namespace Nocturne.Desktop.Tray.Helpers;

/// <summary>
/// Maps Nightscout direction strings to Segoe Fluent Icons arrow glyphs.
/// Windows-specific rendering methods live here; platform-independent arrow text
/// and direction labels are delegated to <see cref="DirectionHelper"/>.
/// </summary>
public static class TrendHelper
{
    /// <summary>Segoe Fluent Icons upward arrow, the glyph <see cref="GetArrowRotation"/> rotates.</summary>
    public const string ArrowUpGlyph = "\uE74A";

    /// <summary>
    /// Segoe Fluent Icons glyph for a direction no arrow can express. Rendered unrotated:
    /// a rotated glyph would report a trend the CGM never sent, and 90 degrees reads as stable.
    /// </summary>
    public const string UnknownGlyph = "\uE9CE";

    /// <summary>
    /// Returns a Segoe Fluent Icons glyph for the given direction.
    /// Windows-specific: uses Segoe Fluent Icon font codepoints.
    /// </summary>
    public static string GetArrowGlyph(string? direction)
    {
        return direction switch
        {
            "TripleUp" => ArrowUpGlyph,
            "DoubleUp" => ArrowUpGlyph,
            "SingleUp" => ArrowUpGlyph,
            "FortyFiveUp" => "\uE76C",
            "Flat" => "\uE76C",
            "FortyFiveDown" => "\uE76C",
            "SingleDown" => "\uE74B",
            "DoubleDown" => "\uE74B",
            "TripleDown" => "\uE74B",
            _ => UnknownGlyph,
        };
    }

    /// <summary>
    /// Returns the rotation angle for <see cref="ArrowUpGlyph"/>, or <c>null</c> when no arrow
    /// can express the direction. Callers must then render <see cref="UnknownGlyph"/> unrotated.
    /// Windows-specific: used with WinUI RotateTransform.
    /// </summary>
    public static double? GetArrowRotation(string? direction)
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
            _ => null,
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
