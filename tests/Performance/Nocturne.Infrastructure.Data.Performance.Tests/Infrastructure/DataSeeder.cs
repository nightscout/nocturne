using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Performance.Tests.Infrastructure;

public static class DataSeeder
{
    private static readonly Random Rng = new(42);

    public static async Task SeedSensorGlucoseAsync(
        NocturneDbContext context, Guid tenantId, int count, CancellationToken ct = default)
    {
        var baseTime = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int batchSize = 5000;

        for (int batch = 0; batch < count; batch += batchSize)
        {
            var chunk = Math.Min(batchSize, count - batch);
            for (int i = 0; i < chunk; i++)
            {
                var idx = batch + i;
                var ts = baseTime.AddMinutes(idx * 5);
                var hoursIntoDay = ts.TimeOfDay.TotalHours;
                var mealEffect = 30 * Math.Sin((hoursIntoDay - 8) * Math.PI / 4)
                               + 20 * Math.Sin((hoursIntoDay - 13) * Math.PI / 3)
                               + 25 * Math.Sin((hoursIntoDay - 19) * Math.PI / 3);
                var noise = (Rng.NextDouble() - 0.5) * 20;
                var mgdl = Math.Clamp(120 + mealEffect + noise, 40, 400);

                context.SensorGlucose.Add(new SensorGlucoseEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Timestamp = ts,
                    Mgdl = mgdl,
                    Direction = "Flat",
                    SysCreatedAt = DateTime.UtcNow,
                    SysUpdatedAt = DateTime.UtcNow,
                });
            }

            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
        }
    }

    public static async Task SeedBolusesAsync(
        NocturneDbContext context, Guid tenantId, int count, CancellationToken ct = default)
    {
        var baseTime = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int batchSize = 1000;

        for (int batch = 0; batch < count; batch += batchSize)
        {
            var chunk = Math.Min(batchSize, count - batch);
            for (int i = 0; i < chunk; i++)
            {
                var idx = batch + i;
                context.Boluses.Add(new BolusEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Timestamp = baseTime.AddMinutes(idx * 60),
                    Insulin = Math.Round(0.5 + Rng.NextDouble() * 9.5, 1),
                    BolusType = "Normal",
                    BolusKind = "Manual",
                    Automatic = false,
                    SysCreatedAt = DateTime.UtcNow,
                    SysUpdatedAt = DateTime.UtcNow,
                });
            }

            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
        }
    }

    public static async Task SeedLinkedRecordsAsync(
        NocturneDbContext context, Guid tenantId,
        string recordType, IReadOnlyList<Guid> recordIds,
        double duplicatePercent, CancellationToken ct = default)
    {
        var dupeCount = (int)(recordIds.Count * duplicatePercent);
        var indices = Enumerable.Range(0, recordIds.Count)
            .OrderBy(_ => Rng.Next())
            .Take(dupeCount)
            .ToList();

        const int batchSize = 1000;
        for (int batch = 0; batch < indices.Count; batch += batchSize)
        {
            var chunk = indices.Skip(batch).Take(batchSize);
            foreach (var idx in chunk)
            {
                var canonicalId = Guid.CreateVersion7();

                // Primary record
                context.LinkedRecords.Add(new LinkedRecordEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    CanonicalId = canonicalId,
                    RecordType = recordType,
                    RecordId = recordIds[idx],
                    SourceTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    DataSource = "source-a",
                    IsPrimary = true,
                    SysCreatedAt = DateTime.UtcNow,
                });

                // Non-primary duplicate (this is what gets filtered out)
                context.LinkedRecords.Add(new LinkedRecordEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    CanonicalId = canonicalId,
                    RecordType = recordType,
                    RecordId = Guid.CreateVersion7(), // fake dupe record ID
                    SourceTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    DataSource = "source-b",
                    IsPrimary = false,
                    SysCreatedAt = DateTime.UtcNow,
                });
            }

            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
        }
    }
}
