using System.Globalization;
using System.Text.RegularExpressions;

namespace Nocturne.Connectors.Core.Utilities;

/// <summary>
///     Centralized timestamp parsing utilities for various connector API formats.
///     Each connector API may return timestamps in different formats; this class provides
///     consistent, resilient parsing across all connectors.
/// </summary>
public static partial class TimestampParser
{
    /// <summary>
    ///     Parses LibreLinkUp timestamp format which uses locale-dependent date strings.
    ///     LibreLinkUp uses formats like "1/14/2025 3:30:00 PM" (US) or "14/1/2025 15:30:00" (EU).
    /// </summary>
    /// <param name="value">The timestamp string from LibreLinkUp API</param>
    /// <returns>Parsed DateTime in UTC</returns>
    /// <exception cref="FormatException">Thrown if the timestamp cannot be parsed</exception>
    public static DateTime ParseLibreFormat(string value)
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

    /// <summary>
    ///     Parses Dexcom Share timestamp format: /Date(milliseconds-offset)/
    ///     Example: "/Date(1426292016000-0700)/"
    /// </summary>
    /// <param name="value">The timestamp string from Dexcom API</param>
    /// <returns>Parsed DateTime in UTC</returns>
    /// <exception cref="FormatException">Thrown if the timestamp cannot be parsed</exception>
    public static DateTime ParseDexcomFormat(string value)
    {
        var match = DexcomTimestampRegex().Match(value);
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
    public static bool TryParseDexcomFormat(string value, out DateTime result)
    {
        result = default;
        var match = DexcomTimestampRegex().Match(value);
        if (!match.Success) return false;

        if (!long.TryParse(match.Groups[1].Value, out var milliseconds)) return false;

        result = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        return true;
    }

    /// <summary>
    ///     Parses ISO8601 format with optional timezone offset correction.
    ///     Used by Glooko which reports local time as if it were UTC.
    /// </summary>
    /// <param name="value">The ISO8601 timestamp string</param>
    /// <param name="timezoneOffsetHours">Hours to subtract to correct for timezone (e.g., -5 for EST)</param>
    /// <returns>Corrected DateTime in UTC</returns>
    public static DateTime ParseIso8601(string value, int timezoneOffsetHours = 0)
    {
        var parsed = DateTime.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        if (timezoneOffsetHours != 0) parsed = parsed.AddHours(-timezoneOffsetHours);

        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
    }

    /// <summary>
    ///     Parses a Unix timestamp in seconds to DateTime.
    /// </summary>
    /// <param name="unixSeconds">Unix timestamp in seconds</param>
    /// <param name="timezoneOffsetHours">Optional timezone offset to apply</param>
    /// <returns>DateTime in UTC</returns>
    public static DateTime FromUnixSeconds(long unixSeconds, int timezoneOffsetHours = 0)
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        return timezoneOffsetHours != 0 ? utc.AddHours(-timezoneOffsetHours) : utc;
    }

    /// <summary>
    ///     Parses a Unix timestamp in milliseconds to DateTime.
    /// </summary>
    /// <param name="unixMilliseconds">Unix timestamp in milliseconds</param>
    /// <param name="timezoneOffsetHours">Optional timezone offset to apply</param>
    /// <returns>DateTime in UTC</returns>
    public static DateTime FromUnixMilliseconds(long unixMilliseconds, int timezoneOffsetHours = 0)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime;
        return timezoneOffsetHours != 0 ? utc.AddHours(-timezoneOffsetHours) : utc;
    }

    /// <summary>
    ///     Tries to parse any common timestamp format, attempting multiple strategies.
    ///     Useful as a fallback when the exact format is unknown.
    /// </summary>
    /// <param name="value">The timestamp string to parse</param>
    /// <param name="result">The parsed DateTime if successful</param>
    /// <returns>True if parsing succeeded, false otherwise</returns>
    public static bool TryParseAny(string value, out DateTime result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(value)) return false;

        // Try Dexcom format first (most distinctive)
        if (value.Contains("/Date(") && TryParseDexcomFormat(value, out result)) return true;

        // Try ISO8601 formats
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result))
        {
            if (result.Kind != DateTimeKind.Utc) result = result.ToUniversalTime();
            return true;
        }

        // Try common formats
        var formats = new[]
        {
            "yyyy-MM-dd'T'HH:mm:ss.fffZ",
            "yyyy-MM-dd'T'HH:mm:ssZ",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
            "M/d/yyyy h:mm:ss tt",
            "M/d/yyyy H:mm:ss",
            "d/M/yyyy H:mm:ss"
        };

        return DateTime.TryParseExact(
            value,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
    }

    [GeneratedRegex(@"\((\d+)")]
    private static partial Regex DexcomTimestampRegex();
}