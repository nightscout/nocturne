namespace Nocturne.Widget.Contracts.Helpers;

/// <summary>
/// Maps Nightscout direction strings to Unicode arrows and human-readable labels.
/// Handles both PascalCase (e.g. "DoubleUp") and UPPERCASE/snake_case (e.g. "DOUBLEUP", "DOUBLE_UP") inputs.
/// </summary>
public static class DirectionHelper
{
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
            "NOT_COMPUTABLE" or "NOTCOMPUTABLE" or "NONE" => "?",
            _ => "?"
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
            "NOT_COMPUTABLE" or "NOTCOMPUTABLE" => "Not computable",
            "RATE_OUT_OF_RANGE" or "RATEOUTOFRANGE" => "Rate out of range",
            _ => "Unknown",
        };
    }

    /// <summary>
    /// Normalizes a direction string by uppercasing and removing underscores,
    /// so that "DoubleUp", "DOUBLE_UP", and "DOUBLEUP" all become "DOUBLEUP".
    /// Special cases like "NOT_COMPUTABLE" and "RATE_OUT_OF_RANGE" are preserved
    /// with underscores to remain distinguishable.
    /// </summary>
    private static string Normalize(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return string.Empty;

        var upper = direction.ToUpperInvariant();

        // Preserve known multi-word special values that use underscores as delimiters
        // between semantically distinct words (NOT_COMPUTABLE, RATE_OUT_OF_RANGE)
        return upper switch
        {
            "NOT COMPUTABLE" => "NOT_COMPUTABLE",
            "RATE OUT OF RANGE" => "RATE_OUT_OF_RANGE",
            _ => upper.Replace("_", ""),
        };
    }
}
