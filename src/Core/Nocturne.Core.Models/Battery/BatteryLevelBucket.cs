namespace Nocturne.Core.Models.Battery;

/// <summary>
/// The icon bucket (0, 25, 50, 75, 100) for a battery percentage. An unknown percentage is 0.
/// </summary>
/// <seealso cref="BatteryReading"/>
/// <seealso cref="BatteryStatistics"/>
public static class BatteryLevelBucket
{
    public static int Of(int? percent) =>
        percent switch
        {
            >= 95 => 100,
            >= 55 => 75,
            >= 30 => 50,
            >= 1 => 25,
            _ => 0,
        };
}
