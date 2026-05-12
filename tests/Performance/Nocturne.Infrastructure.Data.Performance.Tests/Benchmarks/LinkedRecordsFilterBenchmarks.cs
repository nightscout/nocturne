using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Performance.Tests.Infrastructure;

namespace Nocturne.Infrastructure.Data.Performance.Tests.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class LinkedRecordsFilterBenchmarks
{
    private PostgresFixture _fixture = null!;
    private Guid _tenantId;
    [Params(100_000, 500_000, 1_000_000)]
    public int RowCount;

    [Params(0.01, 0.05)]
    public double DuplicatePercent;

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _tenantId = Guid.CreateVersion7();
        await using var ctx = _fixture.CreateContext();
        await DataSeeder.SeedSensorGlucoseAsync(ctx, _tenantId, RowCount);

        if (DuplicatePercent > 0)
        {
            var ids = await ctx.SensorGlucose
                .Where(sg => sg.TenantId == _tenantId)
                .Select(sg => sg.Id)
                .ToListAsync();
            await DataSeeder.SeedLinkedRecordsAsync(
                ctx, _tenantId, "sensorglucose", ids, DuplicatePercent);
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }

    [Benchmark(Description = "WithLinkedRecordsFilter")]
    public async Task<List<SensorGlucoseEntity>> WithFilter()
    {
        if (!_fixture.IsInitialized) return [];
        await using var ctx = _fixture.CreateContext();
        var now = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(RowCount * 5);
        var from = now.AddDays(-7);

        return await ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.TenantId == _tenantId)
            .Where(e => e.Timestamp >= from && e.Timestamp <= now)
            .Where(e => !ctx.LinkedRecords
                .Any(lr => lr.RecordType == "sensorglucose" && !lr.IsPrimary && lr.RecordId == e.Id))
            .OrderByDescending(e => e.Timestamp)
            .Take(100)
            .ToListAsync();
    }

    [Benchmark(Baseline = true, Description = "WithoutLinkedRecordsFilter")]
    public async Task<List<SensorGlucoseEntity>> WithoutFilter()
    {
        if (!_fixture.IsInitialized) return [];
        await using var ctx = _fixture.CreateContext();
        var now = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(RowCount * 5);
        var from = now.AddDays(-7);

        return await ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.TenantId == _tenantId)
            .Where(e => e.Timestamp >= from && e.Timestamp <= now)
            .OrderByDescending(e => e.Timestamp)
            .Take(100)
            .ToListAsync();
    }
}
