namespace Nocturne.Core.Models.Battery;

/// <summary>
/// Picks the reading that represents the uploader battery the way Nightscout's upbat plugin
/// does: per device the lowest reading within ten minutes of its newest, then the weakest device.
/// </summary>
/// <seealso cref="BatteryReading"/>
public static class UploaderBatterySelection
{
    public const int RecentWindowMinutes = 30;

    private const long DeviceRecentMills = 10 * 60 * 1000;

    /// <summary>
    /// Selects from the readings within <see cref="RecentWindowMinutes"/> of <paramref name="nowMills"/>.
    /// </summary>
    public static BatteryReading? Select(IEnumerable<BatteryReading> readings, long nowMills)
    {
        var windowStart = nowMills - RecentWindowMinutes * 60 * 1000;

        return Weakest(
            readings
                .Where(r => r.Mills <= nowMills && r.Mills >= windowStart)
                .GroupBy(r => r.Device)
                .Select(g => RecentLowest(g)));
    }

    /// <summary>
    /// The lowest reading of one device within ten minutes of that device's newest reading.
    /// </summary>
    public static BatteryReading? RecentLowest(IEnumerable<BatteryReading> deviceReadings)
    {
        var readings = deviceReadings.ToList();
        if (readings.Count == 0)
            return null;

        var recentFrom = readings.Max(r => r.Mills) - DeviceRecentMills;
        return readings
            .Where(r => r.Mills > recentFrom)
            .OrderBy(r => r.Battery ?? 100)
            .First();
    }

    public static BatteryReading? Weakest(IEnumerable<BatteryReading?> deviceLowests) =>
        deviceLowests.OfType<BatteryReading>().OrderBy(r => r.Battery ?? 100).FirstOrDefault();
}
