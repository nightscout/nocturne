using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Performance.Tests.Helpers;

public static class SensorGlucoseEntityFactory
{
    private static readonly Random Rng = new(42);

    public static List<SensorGlucoseEntity> Generate(int count)
    {
        var entities = new List<SensorGlucoseEntity>(count);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < count; i++)
        {
            var minutesOffset = i * 5;
            var hoursIntoDay = (minutesOffset % 1440) / 60.0;
            var mealEffect = 30 * Math.Sin((hoursIntoDay - 8) * Math.PI / 4)
                           + 20 * Math.Sin((hoursIntoDay - 13) * Math.PI / 3)
                           + 25 * Math.Sin((hoursIntoDay - 19) * Math.PI / 3);
            var noise = (Rng.NextDouble() - 0.5) * 20;
            var mgdl = Math.Clamp(120 + mealEffect + noise, 40, 400);

            entities.Add(new SensorGlucoseEntity
            {
                Id = Guid.NewGuid(),
                Timestamp = baseTime.AddMinutes(minutesOffset),
                Mgdl = mgdl,
                Direction = "Flat",
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
        }

        return entities;
    }
}
