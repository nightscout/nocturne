using System.Globalization;

namespace Nocturne.Connectors.CareLink.Utilities;

public static class CareLinkTimestampParser
{
    private const double MsPerHour = 3_600_000;

    public static double CalculatePumpOffsetMs(string pumpTimeString, long serverTimeMs)
    {
        if (!DateTime.TryParse(pumpTimeString, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pumpLocal))
            return 0;

        var pumpAsUtcMs = new DateTimeOffset(pumpLocal, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var diffMs = pumpAsUtcMs - serverTimeMs;

        return Math.Round(diffMs / MsPerHour) * MsPerHour;
    }

    public static DateTime? ParseSgTimestamp(string? datetime, double pumpOffsetMs)
    {
        if (string.IsNullOrEmpty(datetime))
            return null;

        if (!DateTime.TryParse(datetime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localTime))
            return null;

        var localMs = new DateTimeOffset(localTime, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var utcMs = localMs - (long)pumpOffsetMs;

        return DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime;
    }
}
