namespace Nocturne.Core.Models;

/// <summary>
/// The precision a record's time is kept at. The legacy APIs name a record by its Unix milliseconds and
/// find it again by that instant, so the store keeps every record time at the millisecond, and a time
/// compared with a stored one is cut the same way first.
/// </summary>
public static class RecordTime
{
    /// <summary><paramref name="value"/> truncated to the millisecond, keeping its <see cref="DateTime.Kind"/>.</summary>
    public static DateTime AtMillisecond(DateTime value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMillisecond));
}
