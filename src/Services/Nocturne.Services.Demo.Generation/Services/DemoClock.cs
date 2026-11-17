namespace Nocturne.Services.Demo.Services;

/// <summary>
/// The frame demo data is generated in: the instant from <paramref name="time"/>, and the zone a
/// demo tenant's days are laid out in. Wall-clock values are Unspecified kind and read in
/// <see cref="Zone"/>, never in the host's zone.
/// </summary>
public sealed class DemoClock(TimeProvider time, TimeZoneInfo zone)
{
    public TimeZoneInfo Zone { get; } = zone;

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    /// <summary>The wall-clock time in <see cref="Zone"/> at <see cref="UtcNow"/>.</summary>
    public DateTime LocalNow => ToLocal(UtcNow);

    /// <summary>
    /// A wall-clock time in <see cref="Zone"/> as UTC; a value already of UTC kind is returned as it
    /// is. A time in a skipped hour reads at the standard offset instead of throwing, as
    /// <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> does, since a day laid out
    /// in five-minute steps walks through it.
    /// </summary>
    public DateTime ToUtc(DateTime local)
    {
        if (local.Kind == DateTimeKind.Utc)
            return local;

        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(wall - Zone.GetUtcOffset(wall), DateTimeKind.Utc);
    }

    /// <summary>A UTC time as a wall-clock time in <see cref="Zone"/>.</summary>
    public DateTime ToLocal(DateTime utc) => DateTime.SpecifyKind(
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone),
        DateTimeKind.Unspecified);
}
