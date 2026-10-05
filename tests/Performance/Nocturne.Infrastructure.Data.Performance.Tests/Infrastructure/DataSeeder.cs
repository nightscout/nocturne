using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Performance.Tests.Shared;

namespace Nocturne.Infrastructure.Data.Performance.Tests.Infrastructure;

public static class DataSeeder
{
    private static readonly Random Rng = new(42);

    public static async Task SeedSensorGlucoseAsync(
        NocturneDbContext context, Guid tenantId, int count, CancellationToken ct = default)
    {
        const int batchSize = 5000;
        var generated = DemoFixtureGenerator.GenerateSensorGlucose(
            count, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var batch = new List<SensorGlucoseEntity>(batchSize);
        foreach (var sensorGlucose in generated)
        {
            var entity = SensorGlucoseMapper.ToEntity(sensorGlucose);
            entity.TenantId = tenantId;
            entity.SysCreatedAt = DateTime.UtcNow;
            entity.SysUpdatedAt = DateTime.UtcNow;
            batch.Add(entity);
            if (batch.Count == batchSize)
            {
                context.SensorGlucose.AddRange(batch);
                await context.SaveChangesAsync(ct);
                context.ChangeTracker.Clear();
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            context.SensorGlucose.AddRange(batch);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
        }
    }

    public static async Task SeedBolusesAsync(
        NocturneDbContext context, Guid tenantId, int count, CancellationToken ct = default)
    {
        const int batchSize = 1000;
        var batch = new List<BolusEntity>(batchSize);
        foreach (var bolus in DemoFixtureGenerator.GenerateBolusStressProjection(
                     count, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
        {
            var entity = BolusMapper.ToEntity(bolus);
            entity.TenantId = tenantId;
            entity.SysCreatedAt = DateTime.UtcNow;
            entity.SysUpdatedAt = DateTime.UtcNow;
            batch.Add(entity);
            if (batch.Count == batchSize)
            {
                context.Boluses.AddRange(batch);
                await context.SaveChangesAsync(ct);
                context.ChangeTracker.Clear();
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            context.Boluses.AddRange(batch);
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
        var indices = Enumerable.Range(0, dupeCount).ToList();

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
                    RecordId = recordIds[dupeCount + idx],
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
                    RecordId = recordIds[idx],
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

    public static async Task SeedTempBasalsAsync(
        NocturneDbContext context, Guid tenantId, int count, CancellationToken ct = default)
    {
        const int batchSize = 1000;
        var batch = new List<TempBasalEntity>(batchSize);
        foreach (var tempBasal in DemoFixtureGenerator.GenerateTempBasals(
                     count, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
        {
            var entity = TempBasalMapper.ToEntity(tempBasal);
            entity.TenantId = tenantId;
            entity.SysCreatedAt = DateTime.UtcNow;
            entity.SysUpdatedAt = DateTime.UtcNow;
            batch.Add(entity);
            if (batch.Count == batchSize)
            {
                context.TempBasals.AddRange(batch);
                await context.SaveChangesAsync(ct);
                context.ChangeTracker.Clear();
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            context.TempBasals.AddRange(batch);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
        }
    }

    public static async Task SeedCarbIntakesAsync(
        NocturneDbContext context, Guid tenantId, int count, CancellationToken ct = default)
    {
        const int batchSize = 1000;
        var batch = new List<CarbIntakeEntity>(batchSize);
        foreach (var carbIntake in DemoFixtureGenerator.GenerateCarbStressProjection(
                     count, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
        {
            var entity = CarbIntakeMapper.ToEntity(carbIntake);
            entity.TenantId = tenantId;
            entity.SysCreatedAt = DateTime.UtcNow;
            entity.SysUpdatedAt = DateTime.UtcNow;
            batch.Add(entity);
            if (batch.Count == batchSize)
            {
                context.CarbIntakes.AddRange(batch);
                await context.SaveChangesAsync(ct);
                context.ChangeTracker.Clear();
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            context.CarbIntakes.AddRange(batch);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
        }
    }
}
