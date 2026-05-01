using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
        _repository = new ApsSnapshotRepository(_context, NullLogger<ApsSnapshotRepository>.Instance);
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

    // --- GetLatestTimestampAsync ---

    [Fact]
    public async Task GetLatestTimestampAsync_returns_null_when_no_rows()
    {
        var result = await _repository.GetLatestTimestampAsync(asOf: null, CancellationToken.None);

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

        var result = await _repository.GetLatestTimestampAsync(asOf: null, CancellationToken.None);

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
        var result = await _repository.GetLatestTimestampAsync(asOf, CancellationToken.None);

        result.Should().Be(t2);
    }

    [Fact]
    public async Task GetLatestTimestampAsync_respects_tenant_isolation()
    {
        var theirRow = new DateTime(2026, 4, 30, 23, 0, 0, DateTimeKind.Utc);
        var ourRow = new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantB, (theirRow, true, 1.0));
        await SeedAsync(TenantA, (ourRow, true, 1.0));

        var result = await _repository.GetLatestTimestampAsync(asOf: null, CancellationToken.None);

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
}
