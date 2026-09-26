using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class ApsSnapshotRepositoryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private readonly NocturneDbContext _context;
    private readonly ApsSnapshotRepository _repository;

    public ApsSnapshotRepositoryTests()
    {
        // Share a single in-memory database between tenant contexts in a test by using a fixed name.
        var dbName = $"aps_snapshot_tests_{Guid.NewGuid()}";
        _context = TestDbContextFactory.CreateInMemoryContext(dbName);
        _context.TenantId = TenantA;
        _repository = new ApsSnapshotRepository(new TestTenantDbContextFactory(_context), new SystemAuditContext(), NullLogger<ApsSnapshotRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync(Guid tenantId, params (DateTime ts, bool enacted, double? sensitivity)[] rows)
    {
        foreach (var (ts, enacted, sensitivity) in rows)
        {
            _context.ApsSnapshots.Add(new ApsSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Timestamp = ts,
                UtcOffset = 0,
                AidAlgorithm = "Loop",
                Enacted = enacted,
                SensitivityRatio = sensitivity,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
        }
        await _context.SaveChangesAsync();
    }

    private async Task SeedSourceAsync(Guid tenantId, params (DateTime ts, string? source)[] rows)
    {
        foreach (var (ts, source) in rows)
        {
            _context.ApsSnapshots.Add(new ApsSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Timestamp = ts,
                UtcOffset = 0,
                AidAlgorithm = "Loop",
                DataSource = source,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
        }
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetAsync_FiltersBySource()
    {
        // The list read advertises a source filter on GET /api/v4/device-status/aps; dropping the
        // predicate returned every connector's snapshots under a single connector's name.
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedSourceAsync(TenantA,
            (t1, "carelink"),
            (t2, "nightscout"));

        var carelink = await _repository.GetAsync(null, null, device: null, source: "carelink");
        carelink.Should().ContainSingle().Which.Timestamp.Should().Be(t1);

        var unfiltered = await _repository.GetAsync(null, null, device: null, source: null);
        unfiltered.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetLatestTimestampAsync_FiltersBySource()
    {
        // Regression: the device-status resume watermark is source-scoped. A 2nd connector's first
        // sync must NOT inherit the 1st connector's later timestamp — a tenant-global latest would
        // mis-classify the new connector's first sync as incremental and skip its backfill.
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedSourceAsync(TenantA,
            (t1, "carelink"),
            (t2, "nightscout"));

        // carelink ignores nightscout's later row.
        (await _repository.GetLatestTimestampAsync("carelink")).Should().Be(t1);
        (await _repository.GetLatestTimestampAsync("nightscout")).Should().Be(t2);
        // A brand-new connector sees no prior data → triggers backfill.
        (await _repository.GetLatestTimestampAsync("never-synced")).Should().BeNull();
        // Unfiltered path still returns the global max.
        (await _repository.GetLatestTimestampAsync(null)).Should().Be(t2);
    }

    // --- GetLatestTimestampAsync ---

    [Fact]
    public async Task GetLatestTimestampAsync_returns_null_when_no_rows()
    {
        var result = await _repository.GetLatestTimestampAsOfAsync(asOf: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestTimestampAsync_returns_latest_when_asOf_null()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA,
            (t1, false, null),
            (t2, false, null),
            (t3, false, null));

        var result = await _repository.GetLatestTimestampAsOfAsync(asOf: null, CancellationToken.None);

        result.Should().Be(t2);
    }

    [Fact]
    public async Task GetLatestTimestampAsync_filters_by_asOf()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA,
            (t1, false, null),
            (t2, false, null),
            (t3, false, null));

        var asOf = new DateTime(2026, 4, 30, 11, 30, 0, DateTimeKind.Utc);
        var result = await _repository.GetLatestTimestampAsOfAsync(asOf, CancellationToken.None);

        result.Should().Be(t2);
    }

    [Fact]
    public async Task GetLatestTimestampAsync_respects_tenant_isolation()
    {
        var theirRow = new DateTime(2026, 4, 30, 23, 0, 0, DateTimeKind.Utc);
        var ourRow = new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantB, (theirRow, true, 1.0));
        await SeedAsync(TenantA, (ourRow, true, 1.0));

        var result = await _repository.GetLatestTimestampAsOfAsync(asOf: null, CancellationToken.None);

        result.Should().Be(ourRow);
    }

    // --- GetLatestEnactedTimestampAsync ---

    [Fact]
    public async Task GetLatestEnactedTimestampAsync_returns_null_when_no_rows()
    {
        var result = await _repository.GetLatestEnactedTimestampAsync(asOf: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestEnactedTimestampAsync_ignores_non_enacted_snapshots()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc); // newer but not enacted
        var t3 = new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA,
            (t1, true, null),
            (t2, false, null),
            (t3, true, null));

        var result = await _repository.GetLatestEnactedTimestampAsync(asOf: null, CancellationToken.None);

        result.Should().Be(t3);
    }

    [Fact]
    public async Task GetLatestEnactedTimestampAsync_filters_by_asOf()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 13, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA,
            (t1, true, null),
            (t2, true, null));

        var asOf = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var result = await _repository.GetLatestEnactedTimestampAsync(asOf, CancellationToken.None);

        result.Should().Be(t1);
    }

    [Fact]
    public async Task GetLatestEnactedTimestampAsync_respects_tenant_isolation()
    {
        var theirRow = new DateTime(2026, 4, 30, 23, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantB, (theirRow, true, null));

        var result = await _repository.GetLatestEnactedTimestampAsync(asOf: null, CancellationToken.None);

        result.Should().BeNull();
    }

    // --- GetLatestSensitivityRatioAsync ---

    [Fact]
    public async Task GetLatestSensitivityRatioAsync_returns_null_when_no_rows()
    {
        var result = await _repository.GetLatestSensitivityRatioAsync(asOf: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestSensitivityRatioAsync_skips_null_sensitivity()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc); // newer but null sensitivity
        var t3 = new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA,
            (t1, false, 0.8),
            (t2, false, null),
            (t3, false, 1.2));

        var result = await _repository.GetLatestSensitivityRatioAsync(asOf: null, CancellationToken.None);

        result.Should().Be(1.2m);
    }

    [Fact]
    public async Task GetLatestSensitivityRatioAsync_filters_by_asOf()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 13, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA,
            (t1, false, 0.9),
            (t2, false, 1.1));

        var asOf = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var result = await _repository.GetLatestSensitivityRatioAsync(asOf, CancellationToken.None);

        result.Should().Be(0.9m);
    }

    [Fact]
    public async Task GetLatestSensitivityRatioAsync_respects_tenant_isolation()
    {
        var ts = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantB, (ts, false, 1.5));

        var result = await _repository.GetLatestSensitivityRatioAsync(asOf: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestSensitivityRatioAsync_returns_null_for_positive_infinity()
    {
        var ts = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA, (ts, false, double.PositiveInfinity));

        var result = await _repository.GetLatestSensitivityRatioAsync(asOf: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestSensitivityRatioAsync_returns_null_for_nan()
    {
        var ts = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA, (ts, false, double.NaN));

        var result = await _repository.GetLatestSensitivityRatioAsync(asOf: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetModifiedSinceAsync_PagesOnWriteClock_SoLateUploadIsDelivered()
    {
        // The record reports srvModified as its write clock, so the query pages on SysUpdatedAt. A
        // row whose event timestamp predates the cursor but whose write clock is newer is delivered;
        // one already at the cursor is not.
        var cursor = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedModifiedAsync(TenantA,
            (cursor.AddHours(-2), cursor),                // write exactly at the cursor -> excluded
            (cursor.AddHours(-1), cursor.AddMinutes(1))); // late upload: write newer -> delivered

        var cursorMills = Mills(cursor);
        var result = (await _repository.GetModifiedSinceAsync(cursorMills)).ToList();

        result.Should().ContainSingle()
            .Which.ModifiedAt.Should().Be(cursor.AddMinutes(1));
    }

    [Fact]
    public async Task GetModifiedSinceAsync_MillisecondHoldingMoreThanLimit_ComesBackInOnePageAndAdvances()
    {
        // A millisecond holding at least `limit` rows must not be re-served: the cursor is that
        // millisecond, so a page cut inside it never advances. The page is extended to the end of
        // the millisecond instead.
        var at = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedModifiedAsync(TenantA, Enumerable.Range(1, 8)
            .Select(_ => (at, at))
            .ToArray());

        var start = Mills(at) - 1;
        var first = (await _repository.GetModifiedSinceAsync(start, limit: 3)).ToList();

        first.Should().HaveCount(8);

        var second = (await _repository.GetModifiedSinceAsync(Mills(first.Max(a => a.ModifiedAt)), limit: 3)).ToList();

        second.Should().BeEmpty();
    }

    [Fact]
    public async Task GetModifiedSinceAsync_TieGroupSplitByLimit_DeliveredOnceAcrossPages()
    {
        // Three rows share a write millisecond; limit 2 would cut the group and re-serve its rows
        // while the cursor stayed put.
        var at = new DateTime(2026, 4, 30, 12, 1, 0, DateTimeKind.Utc);
        var later = at.AddMinutes(1);
        var ids = await SeedModifiedAsync(TenantA,
            (at.AddTicks(10), at),
            (at.AddTicks(20), at),
            (at.AddTicks(30), at),
            (later, later));

        var delivered = new List<Guid>();
        var cursor = Mills(at) - 1;
        for (var page = 0; page < 10; page++)
        {
            var rows = (await _repository.GetModifiedSinceAsync(cursor, limit: 2)).ToList();
            if (rows.Count == 0)
                break;

            delivered.AddRange(rows.Select(r => r.Id));
            cursor = rows.Max(r => Mills(r.ModifiedAt));
        }

        delivered.Should().BeEquivalentTo(ids);
        delivered.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetModifiedSinceAsync_RowOnMillisecondBoundary_IsNotSkipped()
    {
        var boundary = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var nextMillisecond = boundary.AddMilliseconds(1);
        await SeedModifiedAsync(TenantA,
            (boundary, boundary),
            (nextMillisecond, nextMillisecond));

        var start = Mills(boundary) - 1;
        var first = (await _repository.GetModifiedSinceAsync(start, limit: 1)).ToList();
        first.Should().ContainSingle().Which.ModifiedAt.Should().Be(boundary);

        var second = (await _repository.GetModifiedSinceAsync(Mills(first[0].ModifiedAt), limit: 1)).ToList();
        second.Should().ContainSingle().Which.ModifiedAt.Should().Be(nextMillisecond);
    }

    private async Task<List<Guid>> SeedModifiedAsync(
        Guid tenantId, params (DateTime Timestamp, DateTime SysUpdatedAt)[] rows)
    {
        var entities = rows.Select(r => new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Timestamp = r.Timestamp,
            UtcOffset = 0,
            AidAlgorithm = "Loop",
            SysCreatedAt = DateTime.UtcNow,
        }).ToList();

        foreach (var entity in entities)
            _context.ApsSnapshots.Add(entity);
        await _context.SaveChangesAsync();

        // SaveChanges stamped SysUpdatedAt = UtcNow on insert; a second save that touches only that
        // column keeps the assigned value, letting a test place rows either side of the cursor.
        for (var i = 0; i < entities.Count; i++)
            entities[i].SysUpdatedAt = rows[i].SysUpdatedAt;
        await _context.SaveChangesAsync();

        return entities.Select(e => e.Id).ToList();
    }

    private static long Mills(DateTime value) =>
        new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeMilliseconds();
}
