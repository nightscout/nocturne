namespace Nocturne.Widget.Contracts.Helpers;

/// <summary>
/// Maps Nightscout direction strings to the marks the desktop surfaces render: Unicode arrows,
/// human-readable labels, and Segoe Fluent Icons glyphs with the rotation each one needs.
/// Sole owner of the direction vocabulary for the tray and the Windows widget; the web app's
/// equivalent is <c>@nocturne/ui/glucose</c>.
/// </summary>
/// <remarks>
/// A direction the CGM did not report, or reported as unusable, must never render as a stable
/// one, so every such value gets an explicit unknown or warning mark rather than an arrow.
/// </remarks>
public static class DirectionHelper
{
    /// <summary>Unicode mark for a direction no arrow can express.</summary>
    public const string UnknownArrow = "?";

    /// <summary>Segoe Fluent Icons upward arrow, the glyph <see cref="GetFluentRotation"/> rotates.</summary>
    public const string FluentArrowUpGlyph = "\uE74A";

    /// <summary>
    /// Segoe Fluent Icons glyph for a direction no arrow can express. Rendered unrotated:
    /// a rotated glyph would report a trend the CGM never sent, and 90 degrees reads as stable.
    /// </summary>
    public const string FluentUnknownGlyph = "\uE9CE";

    /// <summary>
    /// Segoe Fluent Icons glyph for a direction the CGM reported as unusable - the rate left the
    /// measurable range, or the sensor errored. Distinct from <see cref="FluentUnknownGlyph"/>,
    /// which stands for a trend that simply never arrived.
    /// </summary>
    public const string FluentWarningGlyph = "\uE7BA";

    private const string FluentArrowDownGlyph = "\uE74B";

    private const string FluentChevronRightGlyph = "\uE76C";

    /// <summary>
    /// Returns a Unicode arrow character for the given direction string.
    /// </summary>
    public static string GetArrowText(string? direction)
    {
        return Normalize(direction) switch
        {
            "TRIPLEUP" => "\u2191\u2191",
            "DOUBLEUP" => "\u21C8",
            "SINGLEUP" or "UP" => "\u2191",
            "FORTYFIVEUP" => "\u2197",
            "FLAT" => "\u2192",
            "FORTYFIVEDOWN" => "\u2198",
            "SINGLEDOWN" or "DOWN" => "\u2193",
            "DOUBLEDOWN" => "\u21CA",
            "TRIPLEDOWN" => "\u2193\u2193",
            "RATEOUTOFRANGE" => "\u21D5",
            _ => UnknownArrow,
        };
    }

    /// <summary>
    /// Returns a human-readable label for the given direction string (e.g. "Rising slowly").
    /// </summary>
    public static string GetDirectionLabel(string? direction)
    {
        return Normalize(direction) switch
        {
            "TRIPLEUP" => "Rising very rapidly",
            "DOUBLEUP" => "Rising rapidly",
            "SINGLEUP" or "UP" => "Rising",
            "FORTYFIVEUP" => "Rising slowly",
            "FLAT" => "Stable",
            "FORTYFIVEDOWN" => "Falling slowly",
            "SINGLEDOWN" or "DOWN" => "Falling",
            "DOUBLEDOWN" => "Falling rapidly",
            "TRIPLEDOWN" => "Falling very rapidly",
            "NOTCOMPUTABLE" => "Not computable",
            "RATEOUTOFRANGE" => "Rate out of range",
            "CGMERROR" => "Sensor error",
            _ => "Unknown",
        };
    }

    /// <summary>
    /// Returns a Segoe Fluent Icons glyph for the given direction. Windows-specific: the
    /// codepoints resolve only in that font.
    /// </summary>
    public static string GetFluentGlyph(string? direction)
    {
        return Normalize(direction) switch
        {
            "TRIPLEUP" or "DOUBLEUP" or "SINGLEUP" or "UP" => FluentArrowUpGlyph,
            "FORTYFIVEUP" or "FLAT" or "FORTYFIVEDOWN" => FluentChevronRightGlyph,
            "SINGLEDOWN" or "DOWN" or "DOUBLEDOWN" or "TRIPLEDOWN" => FluentArrowDownGlyph,
            "RATEOUTOFRANGE" or "CGMERROR" => FluentWarningGlyph,
            _ => FluentUnknownGlyph,
        };
    }

    /// <summary>
    /// Returns the rotation angle for <see cref="FluentArrowUpGlyph"/>, or <c>null</c> when no
    /// arrow can express the direction. Callers must then render <see cref="GetFluentGlyph"/>
    /// unrotated. Windows-specific: used with WinUI RotateTransform.
    /// </summary>
    public static double? GetFluentRotation(string? direction)
    {
        return Normalize(direction) switch
        {
            "TRIPLEUP" or "DOUBLEUP" or "SINGLEUP" or "UP" => 0,
            "FORTYFIVEUP" => 45,
            "FLAT" => 90,
            "FORTYFIVEDOWN" => 135,
            "SINGLEDOWN" or "DOWN" or "DOUBLEDOWN" or "TRIPLEDOWN" => 180,
            _ => null,
        };
    }

    /// <summary>
    /// Folds every casing and separator variant a caller may hold onto one key, so that
    /// "FortyFiveUp", "FORTY_FIVE_UP" and "forty five up" all become "FORTYFIVEUP".
    /// </summary>
    public static string Normalize(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return string.Empty;

        return string.Concat(direction.Where(char.IsLetter)).ToUpperInvariant();
    }
}
