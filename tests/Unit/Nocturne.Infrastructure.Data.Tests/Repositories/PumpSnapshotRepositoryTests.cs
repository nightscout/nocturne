using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class PumpSnapshotRepositoryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private readonly NocturneDbContext _context;
    private readonly PumpSnapshotRepository _repository;

    public PumpSnapshotRepositoryTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = TenantA;
        _repository = new PumpSnapshotRepository(_context, NullLogger<PumpSnapshotRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync(Guid tenantId, params (DateTime ts, int? batteryPercent)[] rows)
    {
        foreach (var (ts, battery) in rows)
        {
            _context.PumpSnapshots.Add(new PumpSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Timestamp = ts,
                UtcOffset = 0,
                BatteryPercent = battery,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
        }
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetLatestAsync_returns_null_when_no_rows()
    {
        var result = await _repository.GetLatestAsync(asOf: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestAsync_returns_latest_when_asOf_null()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA, (t1, 80), (t2, 70));

        var result = await _repository.GetLatestAsync(asOf: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Timestamp.Should().Be(t2);
        result.BatteryPercent.Should().Be(70);
    }

    [Fact]
    public async Task GetLatestAsync_filters_by_asOf_inclusive()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA, (t1, 80), (t2, 70));

        // asOf == t2 (boundary): should include t2 (inclusive <=)
        var result = await _repository.GetLatestAsync(asOf: t2, CancellationToken.None);

        result!.Timestamp.Should().Be(t2);

        // asOf < t2: should return t1
        var earlier = await _repository.GetLatestAsync(
            asOf: new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);
        earlier!.Timestamp.Should().Be(t1);
    }

    [Fact]
    public async Task GetLatestAsync_respects_tenant_isolation()
    {
        var theirRow = new DateTime(2026, 4, 30, 23, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantB, (theirRow, 5));

        var result = await _repository.GetLatestAsync(asOf: null, CancellationToken.None);

        result.Should().BeNull();
    }
}
