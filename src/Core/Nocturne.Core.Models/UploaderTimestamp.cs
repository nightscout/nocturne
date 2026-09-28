using System.Globalization;

namespace Nocturne.Core.Models;

/// <summary>
/// Parses timestamp strings sent by uploaders, remote Nightscout-compatible sources and query
/// strings.
/// </summary>
/// <remarks>
/// An explicit <c>Z</c> or offset is honoured and kept on the result; a zone-less string is read
/// as UTC rather than as the API host's local time, so the instant it resolves to never depends
/// on the host's time zone. Culture-invariant, so an ambiguous numeric date is always read
/// month-first.
/// </remarks>
public static class UploaderTimestamp
{
    public static bool TryParse(string? value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out result
        );

    /// <summary>The parsed instant as a UTC <see cref="DateTime"/>, or null when it does not parse.</summary>
    public static DateTime? ParseUtcDateTime(string? value) =>
        TryParse(value, out var parsed) ? parsed.UtcDateTime : null;
}
