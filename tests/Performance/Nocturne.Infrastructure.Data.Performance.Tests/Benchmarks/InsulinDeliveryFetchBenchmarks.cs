using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Performance.Tests.Infrastructure;

namespace Nocturne.Infrastructure.Data.Performance.Tests.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class InsulinDeliveryFetchBenchmarks
{
    private PostgresFixture _fixture = null!;
    private Guid _tenantId;

    [Params(30, 90)]
    public int Days;

    private DateTime _from;
    private DateTime _to;

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _tenantId = Guid.CreateVersion7();
        await using var ctx = _fixture.CreateContext();

        // Seed realistic IDP dataset
        var bolusCount = Days * 24;        // ~1 bolus/hr
        var algoBolusCount = Days * 24;
        var tempBasalCount = Days * 288;   // every 5 minutes
        var carbCount = Days * 10;         // ~10 carb entries/day

        await DataSeeder.SeedBolusesAsync(ctx, _tenantId, bolusCount + algoBolusCount);
        await DataSeeder.SeedTempBasalsAsync(ctx, _tenantId, tempBasalCount);
        await DataSeeder.SeedCarbIntakesAsync(ctx, _tenantId, carbCount);

        // _to is based on the bolus seeder's baseTime + all bolus minutes
        _from = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _to = _from.AddDays(Days + 1);
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _fixture.DisposeAsync();

    [Benchmark(Baseline = true, Description = "Sequential_4Queries")]
    public async Task SequentialFetch()
    {
        await using var ctx = _fixture.CreateContext();

        _ = await ctx.Boluses.AsNoTracking()
            .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to
                && e.BolusKind == "Manual")
            .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();

        _ = await ctx.TempBasals.AsNoTracking()
            .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to)
            .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();

        _ = await ctx.Boluses.AsNoTracking()
            .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to
                && e.BolusKind == "Algorithm")
            .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();

        _ = await ctx.CarbIntakes.AsNoTracking()
            .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to)
            .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();
    }

    [Benchmark(Description = "Parallel_4Queries")]
    public async Task ParallelFetch()
    {
        var bolusTask = Task.Run(async () =>
        {
            await using var ctx = _fixture.CreateContext();
            return await ctx.Boluses.AsNoTracking()
                .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to
                    && e.BolusKind == "Manual")
                .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();
        });

        var tempBasalTask = Task.Run(async () =>
        {
            await using var ctx = _fixture.CreateContext();
            return await ctx.TempBasals.AsNoTracking()
                .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to)
                .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();
        });

        var algoTask = Task.Run(async () =>
        {
            await using var ctx = _fixture.CreateContext();
            return await ctx.Boluses.AsNoTracking()
                .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to
                    && e.BolusKind == "Algorithm")
                .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();
        });

        var carbTask = Task.Run(async () =>
        {
            await using var ctx = _fixture.CreateContext();
            return await ctx.CarbIntakes.AsNoTracking()
                .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to)
                .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();
        });

        await Task.WhenAll(bolusTask, tempBasalTask, algoTask, carbTask);
    }
}
