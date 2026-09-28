using Nocturne.Core.Models;

namespace Nocturne.Connectors.CareLink.Utilities;

public static class CareLinkTimestampParser
{
    private const double MsPerHour = 3_600_000;

    public static double CalculatePumpOffsetMs(string pumpTimeString, long serverTimeMs)
    {
        if (!UploaderTimestamp.TryParse(pumpTimeString, out var pumpLocal))
            return 0;

        var pumpAsUtcMs = pumpLocal.ToUnixTimeMilliseconds();
        var diffMs = pumpAsUtcMs - serverTimeMs;

        return Math.Round(diffMs / MsPerHour) * MsPerHour;
    }

    public static DateTime? ParseSgTimestamp(string? datetime, double pumpOffsetMs)
    {
        if (string.IsNullOrEmpty(datetime))
            return null;

        if (!UploaderTimestamp.TryParse(datetime, out var localTime))
            return null;

        var localMs = localTime.ToUnixTimeMilliseconds();
        var utcMs = localMs - (long)pumpOffsetMs;

        return DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime;
    }
}
