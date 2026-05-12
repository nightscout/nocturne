using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Performance.Tests.Helpers;

public static class BolusEntityFactory
{
    private static readonly Random Rng = new(42);

    public static List<BolusEntity> Generate(int count)
    {
        var entities = new List<BolusEntity>(count);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < count; i++)
        {
            var minutesOffset = i * 60; // hourly boluses
            var insulin = Math.Round(0.5 + Rng.NextDouble() * 9.5, 1); // 0.5 - 10.0 U

            entities.Add(new BolusEntity
            {
                Id = Guid.NewGuid(),
                Timestamp = baseTime.AddMinutes(minutesOffset),
                Insulin = insulin,
                BolusType = "Normal",
                BolusKind = "Manual",
                Automatic = false,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
        }

        return entities;
    }
}
