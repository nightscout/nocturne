using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Performance.Tests.Infrastructure;

namespace Nocturne.Infrastructure.Data.Performance.Tests.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class PaginationBenchmarks
{
    private PostgresFixture _fixture = null!;
    private Guid _tenantId;

    // Fixed at 100k rows — we're varying pagination depth, not data volume
    private const int RowCount = 100_000;

    [Params(0, 1_000, 10_000, 50_000)]
    public int Offset;

    private DateTime _cursorTimestamp;
    private Guid _cursorId;

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _tenantId = Guid.CreateVersion7();
        await using var seedCtx = _fixture.CreateContext();
        await DataSeeder.SeedSensorGlucoseAsync(seedCtx, _tenantId, RowCount);

        // Pre-compute cursor for the given offset position
        if (Offset > 0)
        {
            await using var cursorCtx = _fixture.CreateContext();
            var cursorRow = await cursorCtx.SensorGlucose.AsNoTracking()
                .Where(e => e.TenantId == _tenantId)
                .OrderByDescending(e => e.Timestamp)
                .ThenByDescending(e => e.Id)
                .Skip(Offset)
                .Select(e => new { e.Timestamp, e.Id })
                .FirstOrDefaultAsync();
            if (cursorRow != null)
            {
                _cursorTimestamp = cursorRow.Timestamp;
                _cursorId = cursorRow.Id;
            }
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }

    [Benchmark(Baseline = true, Description = "Offset_Pagination")]
    public async Task<List<SensorGlucoseEntity>> OffsetPagination()
    {
        if (!_fixture.IsInitialized) return [];
        await using var ctx = _fixture.CreateContext();

        return await ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.TenantId == _tenantId)
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Skip(Offset)
            .Take(100)
            .ToListAsync();
    }

    [Benchmark(Description = "Keyset_Pagination")]
    public async Task<List<SensorGlucoseEntity>> KeysetPagination()
    {
        if (!_fixture.IsInitialized) return [];
        await using var ctx = _fixture.CreateContext();

        if (Offset == 0)
        {
            // No cursor for first page — same as offset
            return await ctx.SensorGlucose.AsNoTracking()
                .Where(e => e.TenantId == _tenantId)
                .OrderByDescending(e => e.Timestamp)
                .ThenByDescending(e => e.Id)
                .Take(100)
                .ToListAsync();
        }

        return await ctx.SensorGlucose.AsNoTracking()
            .Where(e => e.TenantId == _tenantId)
            .Where(e => e.Timestamp < _cursorTimestamp
                || (e.Timestamp == _cursorTimestamp && e.Id < _cursorId))
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Take(100)
            .ToListAsync();
    }
}
