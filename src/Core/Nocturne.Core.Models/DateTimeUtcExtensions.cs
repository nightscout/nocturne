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
}
