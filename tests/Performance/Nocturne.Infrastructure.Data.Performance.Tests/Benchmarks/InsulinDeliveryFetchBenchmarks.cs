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

        _tenantId = await _fixture.CreateTenantAsync();
        await using var ctx = _fixture.CreateContext(_tenantId);

        var bolusCount = Days * 24;
        var algoBolusCount = Days * 24;
        var tempBasalCount = Days * 288;
        var carbCount = Days * 10;

        await DataSeeder.SeedBolusesAsync(ctx, _tenantId, bolusCount + algoBolusCount);
        await DataSeeder.SeedTempBasalsAsync(ctx, _tenantId, tempBasalCount);
        await DataSeeder.SeedCarbIntakesAsync(ctx, _tenantId, carbCount);

        _from = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _to = _from.AddDays(Days + 1);
        var manualCount = await ctx.Boluses.CountAsync(e => e.Timestamp >= _from && e.Timestamp <= _to && e.BolusKind == "Manual");
        var algorithmCount = await ctx.Boluses.CountAsync(e => e.Timestamp >= _from && e.Timestamp <= _to && e.BolusKind == "Algorithm");
        var basalCount = await ctx.TempBasals.CountAsync(e => e.Timestamp >= _from && e.Timestamp <= _to);
        var seededCarbCount = await ctx.CarbIntakes.CountAsync(e => e.Timestamp >= _from && e.Timestamp <= _to);
        if (manualCount != bolusCount || algorithmCount != algoBolusCount || basalCount != tempBasalCount || seededCarbCount != carbCount)
            throw new InvalidOperationException(
                $"Expected {bolusCount} manual boluses, {algoBolusCount} algorithm boluses, {tempBasalCount} temp basals, and {carbCount} carb intakes; found {manualCount}, {algorithmCount}, {basalCount}, and {seededCarbCount}.");
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _fixture.DisposeAsync();

    [Benchmark(Baseline = true, Description = "Sequential_4Queries_StressProjection")]
    public async Task SequentialFetch()
    {
        await using var ctx = _fixture.CreateContext(_tenantId);

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

    [Benchmark(Description = "Parallel_4Queries_StressProjection")]
    public async Task ParallelFetch()
    {
        var bolusTask = Task.Run(async () =>
        {
            await using var ctx = _fixture.CreateContext(_tenantId);
            return await ctx.Boluses.AsNoTracking()
                .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to
                    && e.BolusKind == "Manual")
                .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();
        });

        var tempBasalTask = Task.Run(async () =>
        {
            await using var ctx = _fixture.CreateContext(_tenantId);
            return await ctx.TempBasals.AsNoTracking()
                .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to)
                .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();
        });

        var algoTask = Task.Run(async () =>
        {
            await using var ctx = _fixture.CreateContext(_tenantId);
            return await ctx.Boluses.AsNoTracking()
                .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to
                    && e.BolusKind == "Algorithm")
                .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();
        });

        var carbTask = Task.Run(async () =>
        {
            await using var ctx = _fixture.CreateContext(_tenantId);
            return await ctx.CarbIntakes.AsNoTracking()
                .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to)
                .OrderBy(e => e.Timestamp).Take(10000).ToListAsync();
        });

        await Task.WhenAll(bolusTask, tempBasalTask, algoTask, carbTask);
    }

    public async Task<int[]> ValidateWorkloadAsync()
    {
        await using var manualContext = _fixture.CreateContext(_tenantId);
        var manualCount = await manualContext.Boluses.AsNoTracking()
            .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to && e.BolusKind == "Manual")
            .OrderBy(e => e.Timestamp).Take(10000).CountAsync();

        await using var basalContext = _fixture.CreateContext(_tenantId);
        var basalCount = await basalContext.TempBasals.AsNoTracking()
            .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to)
            .OrderBy(e => e.Timestamp).Take(10000).CountAsync();

        await using var algorithmContext = _fixture.CreateContext(_tenantId);
        var algorithmCount = await algorithmContext.Boluses.AsNoTracking()
            .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to && e.BolusKind == "Algorithm")
            .OrderBy(e => e.Timestamp).Take(10000).CountAsync();

        await using var carbContext = _fixture.CreateContext(_tenantId);
        var carbCount = await carbContext.CarbIntakes.AsNoTracking()
            .Where(e => e.TenantId == _tenantId && e.Timestamp >= _from && e.Timestamp <= _to)
            .OrderBy(e => e.Timestamp).Take(10000).CountAsync();

        return [manualCount, basalCount, algorithmCount, carbCount];
    }
}
