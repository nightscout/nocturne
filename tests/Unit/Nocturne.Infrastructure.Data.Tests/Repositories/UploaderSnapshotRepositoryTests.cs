using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class UploaderSnapshotRepositoryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private readonly NocturneDbContext _context;
    private readonly UploaderSnapshotRepository _repository;

    public UploaderSnapshotRepositoryTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = TenantA;
        _repository = new UploaderSnapshotRepository(_context, NullLogger<UploaderSnapshotRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync(Guid tenantId, params (DateTime ts, int? battery, string device)[] rows)
    {
        foreach (var (ts, battery, device) in rows)
        {
            _context.UploaderSnapshots.Add(new UploaderSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Timestamp = ts,
                UtcOffset = 0,
                Battery = battery,
                Device = device,
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
    public async Task GetLatestAsync_returns_only_uploader()
    {
        var ts = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA, (ts, 50, "phone"));

        var result = await _repository.GetLatestAsync(asOf: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Battery.Should().Be(50);
        result.Device.Should().Be("phone");
    }

    [Fact]
    public async Task GetLatestAsync_picks_lowest_battery_among_multiple()
    {
        var ts = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA,
            (ts, 80, "phone"),
            (ts, 25, "tablet"),
            (ts, 50, "watch"));

        var result = await _repository.GetLatestAsync(asOf: null, CancellationToken.None);

        result!.Battery.Should().Be(25);
        result.Device.Should().Be("tablet");
    }

    [Fact]
    public async Task GetLatestAsync_falls_back_to_latest_when_battery_null()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA,
            (t1, null, "phone"),
            (t2, null, "tablet"));

        var result = await _repository.GetLatestAsync(asOf: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Timestamp.Should().Be(t2);
        result.Device.Should().Be("tablet");
    }

    [Fact]
    public async Task GetLatestAsync_orders_nulls_after_known_battery()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 13, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA,
            (t1, 60, "phone"),
            (t2, null, "tablet"));

        // even though tablet is newer, phone with known battery 60 wins (nulls last)
        var result = await _repository.GetLatestAsync(asOf: null, CancellationToken.None);

        result!.Device.Should().Be("phone");
        result.Battery.Should().Be(60);
    }

    [Fact]
    public async Task GetLatestAsync_filters_by_asOf()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 13, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA,
            (t1, 80, "phone"),
            (t2, 10, "tablet"));

        var asOf = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var result = await _repository.GetLatestAsync(asOf, CancellationToken.None);

        result!.Battery.Should().Be(80);
        result.Device.Should().Be("phone");
    }

    [Fact]
    public async Task GetLatestAsync_respects_tenant_isolation()
    {
        var ts = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        // Seed both tenants: prove TenantB's lower-battery row is rejected AND TenantA's row surfaces.
        await SeedAsync(TenantB, (ts, 5, "phone"));
        await SeedAsync(TenantA, (ts, 75, "tablet"));

        var result = await _repository.GetLatestAsync(asOf: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Battery.Should().Be(75);
        result.Device.Should().Be("tablet");
    }
}
