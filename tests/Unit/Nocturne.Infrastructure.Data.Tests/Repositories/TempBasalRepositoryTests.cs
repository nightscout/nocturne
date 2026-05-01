using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class TempBasalRepositoryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private readonly NocturneDbContext _context;
    private readonly TempBasalRepository _repository;

    public TempBasalRepositoryTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = TenantA;
        _repository = new TempBasalRepository(
            _context,
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<TempBasalRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync(Guid tenantId, params (DateTime start, DateTime? end, double rate)[] rows)
    {
        foreach (var (start, end, rate) in rows)
        {
            _context.TempBasals.Add(new TempBasalEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                StartTimestamp = start,
                EndTimestamp = end,
                UtcOffset = 0,
                Rate = rate,
                Origin = "test",
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
        }
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetActiveAtAsync_returns_null_when_no_rows()
    {
        var result = await _repository.GetActiveAtAsync(
            new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAtAsync_returns_active_temp()
    {
        var start = new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA, (start, end, 1.5));

        var result = await _repository.GetActiveAtAsync(
            new DateTime(2026, 4, 30, 11, 30, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Rate.Should().Be(1.5);
    }

    [Fact]
    public async Task GetActiveAtAsync_returns_open_ended_temp()
    {
        var start = new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA, (start, null, 0.8));

        var result = await _repository.GetActiveAtAsync(
            new DateTime(2026, 4, 30, 23, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Rate.Should().Be(0.8);
    }

    [Fact]
    public async Task GetActiveAtAsync_returns_null_when_gap_between_temps()
    {
        await SeedAsync(TenantA,
            (new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc), 1.0),
            (new DateTime(2026, 4, 30, 13, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 30, 14, 0, 0, DateTimeKind.Utc), 1.5));

        var result = await _repository.GetActiveAtAsync(
            new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAtAsync_picks_most_recent_when_overlapping()
    {
        await SeedAsync(TenantA,
            (new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 30, 13, 0, 0, DateTimeKind.Utc), 1.0),
            (new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 30, 14, 0, 0, DateTimeKind.Utc), 2.5));

        var result = await _repository.GetActiveAtAsync(
            new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result!.Rate.Should().Be(2.5);
    }

    [Fact]
    public async Task GetActiveAtAsync_end_is_exclusive()
    {
        var start = new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantA, (start, end, 1.5));

        // 'at' equal to end: not active (end is exclusive per StartTimestamp <= at < EndTimestamp)
        var result = await _repository.GetActiveAtAsync(end, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAtAsync_respects_tenant_isolation()
    {
        var start = new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(TenantB, (start, end, 9.0));

        var result = await _repository.GetActiveAtAsync(
            new DateTime(2026, 4, 30, 11, 30, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result.Should().BeNull();
    }
}
