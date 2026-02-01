using System.Text.RegularExpressions;

namespace Nocturne.Connectors.Dexcom.Utilities;

/// <summary>
///     Timestamp parsing utilities for Dexcom Share API formats.
/// </summary>
public static partial class DexcomTimestampParser
{
    /// <summary>
    ///     Parses Dexcom Share timestamp format: /Date(milliseconds-offset)/
    ///     Example: "/Date(1426292016000-0700)/"
    /// </summary>
    /// <param name="value">The timestamp string from Dexcom API</param>
    /// <returns>Parsed DateTime in UTC</returns>
    /// <exception cref="FormatException">Thrown if the timestamp cannot be parsed</exception>
    public static DateTime Parse(string value)
    {
        var match = TimestampRegex().Match(value);
        if (!match.Success) throw new FormatException($"Invalid Dexcom timestamp format: {value}");

        var milliseconds = long.Parse(match.Groups[1].Value);
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
    }

    /// <summary>
    ///     Tries to parse Dexcom Share timestamp format.
    /// </summary>
    /// <param name="value">The timestamp string from Dexcom API</param>
    /// <param name="result">The parsed DateTime if successful</param>
    /// <returns>True if parsing succeeded, false otherwise</returns>
    public static bool TryParse(string value, out DateTime result)
    {
        result = default;
        var match = TimestampRegex().Match(value);
        if (!match.Success) return false;

        if (!long.TryParse(match.Groups[1].Value, out var milliseconds)) return false;

        result = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        return true;
    }

    [GeneratedRegex(@"\((\d+)")]
    private static partial Regex TimestampRegex();
}
