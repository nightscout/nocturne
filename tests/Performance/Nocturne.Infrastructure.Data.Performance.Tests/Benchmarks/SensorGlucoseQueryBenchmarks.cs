using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Performance.Tests.Infrastructure;

namespace Nocturne.Infrastructure.Data.Performance.Tests.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class SensorGlucoseQueryBenchmarks
{
    private PostgresFixture _fixture = null!;
    private Guid _tenantId;

    [Params(10_000, 100_000, 500_000)]
    public int RowCount;

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _tenantId = Guid.CreateVersion7();
        await using var ctx = _fixture.CreateContext();
        await DataSeeder.SeedSensorGlucoseAsync(ctx, _tenantId, RowCount);

        // Seed 1% linked records for dedup filtering
        var ids = await ctx.SensorGlucose
            .Where(sg => sg.TenantId == _tenantId)
            .Select(sg => sg.Id)
            .ToListAsync();
        await DataSeeder.SeedLinkedRecordsAsync(ctx, _tenantId, "sensorglucose", ids, 0.01);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }

    [Benchmark(Description = "GetAsync_24h_Latest100")]
    public async Task<List<SensorGlucoseEntity>> GetAsync_24h_Latest100()
    {
        if (!_fixture.IsInitialized) return [];
        await using var ctx = _fixture.CreateContext();
        var now = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(RowCount * 5);
        var from = now.AddHours(-24);

        var query = ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.TenantId == _tenantId)
            .Where(e => e.Timestamp >= from && e.Timestamp <= now)
            .Where(e => !ctx.LinkedRecords
                .Any(lr => lr.RecordType == "sensorglucose" && !lr.IsPrimary && lr.RecordId == e.Id))
            .OrderByDescending(e => e.Timestamp)
            .Take(100);

        return await query.ToListAsync();
    }

    [Benchmark(Description = "GetAsync_7d_Latest100")]
    public async Task<List<SensorGlucoseEntity>> GetAsync_7d_Latest100()
    {
        if (!_fixture.IsInitialized) return [];
        await using var ctx = _fixture.CreateContext();
        var now = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(RowCount * 5);
        var from = now.AddDays(-7);

        var query = ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.TenantId == _tenantId)
            .Where(e => e.Timestamp >= from && e.Timestamp <= now)
            .Where(e => !ctx.LinkedRecords
                .Any(lr => lr.RecordType == "sensorglucose" && !lr.IsPrimary && lr.RecordId == e.Id))
            .OrderByDescending(e => e.Timestamp)
            .Take(100);

        return await query.ToListAsync();
    }

    [Benchmark(Description = "GetAsync_NoRange_Latest100")]
    public async Task<List<SensorGlucoseEntity>> GetAsync_NoRange_Latest100()
    {
        if (!_fixture.IsInitialized) return [];
        await using var ctx = _fixture.CreateContext();

        var query = ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.TenantId == _tenantId)
            .Where(e => !ctx.LinkedRecords
                .Any(lr => lr.RecordType == "sensorglucose" && !lr.IsPrimary && lr.RecordId == e.Id))
            .OrderByDescending(e => e.Timestamp)
            .Take(100);

        return await query.ToListAsync();
    }
}
