using Nocturne.Core.Models.Widget;

namespace Nocturne.Desktop.Tray.Extensions;

public static class GlucoseReadingExtensions
{
    public static DateTimeOffset GetTimestamp(this V4GlucoseReading reading)
        => DateTimeOffset.FromUnixTimeMilliseconds(reading.Mills);

    public static TimeSpan GetAge(this V4GlucoseReading reading)
        => DateTimeOffset.UtcNow - reading.GetTimestamp();
}
