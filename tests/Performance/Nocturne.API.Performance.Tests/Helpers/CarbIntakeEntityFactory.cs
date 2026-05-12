using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Performance.Tests.Helpers;

public static class CarbIntakeEntityFactory
{
    private static readonly Random Rng = new(42);

    public static List<CarbIntakeEntity> Generate(int count)
    {
        var entities = new List<CarbIntakeEntity>(count);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < count; i++)
        {
            var minutesOffset = i * 60; // hourly carb entries
            var carbs = Math.Round(5 + Rng.NextDouble() * 95, 0); // 5 - 100 g

            entities.Add(new CarbIntakeEntity
            {
                Id = Guid.NewGuid(),
                Timestamp = baseTime.AddMinutes(minutesOffset),
                Carbs = carbs,
                AbsorptionTime = Rng.Next(2, 6) * 15, // 30, 45, 60, or 75 min
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
        }

        return entities;
    }
}
