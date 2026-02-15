using System.Globalization;

namespace Nocturne.Connectors.FreeStyle.Utilities;

/// <summary>
///     Timestamp parsing utilities for LibreLinkUp API formats.
/// </summary>
public static class LibreTimestampParser
{
    /// <summary>
    ///     Parses LibreLinkUp timestamp format which uses locale-dependent date strings.
    ///     LibreLinkUp uses formats like "1/14/2025 3:30:00 PM" (US) or "14/1/2025 15:30:00" (EU).
    /// </summary>
    /// <param name="value">The timestamp string from LibreLinkUp API</param>
    /// <returns>Parsed DateTime in UTC</returns>
    /// <exception cref="FormatException">Thrown if the timestamp cannot be parsed</exception>
    public static DateTime Parse(string value)
    {
        var formats = new[]
        {
            "M/d/yyyy h:mm:ss tt",
            "M/d/yyyy h:mm tt",
            "M/d/yyyy H:mm:ss",
            "M/d/yyyy H:mm",
            "d/M/yyyy H:mm:ss",
            "d/M/yyyy H:mm",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss"
        };

        if (DateTime.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
            return parsed;

        if (DateTime.TryParse(
                value,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
                out parsed))
            return parsed;

        throw new FormatException($"Invalid LibreLinkUp timestamp: {value}");
    }
}
