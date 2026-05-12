using Nocturne.Core.Models.V4;

namespace Nocturne.API.Performance.Tests.Helpers;

public static class SensorGlucoseFactory
{
    private static readonly Random Rng = new(42); // Fixed seed for reproducibility

    /// <summary>
    /// Generates a realistic CGM dataset with values following a sinusoidal pattern
    /// around a mean glucose of 120 mg/dL, simulating meals and basal drift.
    /// </summary>
    public static List<SensorGlucose> Generate(int count)
    {
        var entries = new List<SensorGlucose>(count);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < count; i++)
        {
            var minutesOffset = i * 5;
            var hoursIntoDay = (minutesOffset % 1440) / 60.0;

            // Sinusoidal meal pattern: peaks at 8am, 1pm, 7pm
            var mealEffect = 30 * Math.Sin((hoursIntoDay - 8) * Math.PI / 4)
                           + 20 * Math.Sin((hoursIntoDay - 13) * Math.PI / 3)
                           + 25 * Math.Sin((hoursIntoDay - 19) * Math.PI / 3);

            var noise = (Rng.NextDouble() - 0.5) * 20;
            var mgdl = Math.Clamp(120 + mealEffect + noise, 40, 400);

            entries.Add(new SensorGlucose
            {
                Id = Guid.NewGuid(),
                Timestamp = baseTime.AddMinutes(minutesOffset),
                Mgdl = mgdl,
                Direction = GlucoseDirection.Flat,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
            });
        }

        return entries;
    }
}
