namespace Nocturne.Core.Models;

/// <summary>
/// Reads a bound <see cref="DateTime"/> as a UTC instant. A zone-less value is UTC, not host local.
/// </summary>
public static class DateTimeUtcExtensions
{
    public static DateTime AsUtc(this DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>The Unix milliseconds of <paramref name="value"/> read as a UTC instant.</summary>
    public static long ToMills(this DateTime value) => new DateTimeOffset(value.AsUtc()).ToUnixTimeMilliseconds();
}
