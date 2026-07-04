using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Covers <see cref="PatientDeviceRepository.ReorderAsync"/> added for the multi-CGM feature:
/// batch rank assignment that returns only the devices whose rank actually changed, skips
/// unchanged ranks, and ignores unknown ids.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "PatientDevice")]
public class PatientDeviceRepositoryReorderTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly DbConnection _connection;
    private readonly NocturneDbContext _context;
    private readonly PatientDeviceRepository _repo;

    public PatientDeviceRepositoryReorderTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using (var seedContext = new NocturneDbContext(options))
        {
            seedContext.TenantId = TestTenantId;
            seedContext.Database.EnsureCreated();
            seedContext.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
            seedContext.SaveChanges();
        }

        _context = new NocturneDbContext(options) { TenantId = TestTenantId };
        _repo = new PatientDeviceRepository(
            new TestTenantDbContextFactory(_context),
            NullLogger<PatientDeviceRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedDevice(int? rank)
    {
        var id = Guid.NewGuid();
        _context.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = id,
            TenantId = TestTenantId,
            DeviceCategory = "CGM",
            Manufacturer = "Dexcom",
            Model = "G7",
            IsCurrent = true,
            Rank = rank,
        });
        _context.SaveChanges();
        return id;
    }

    [Fact]
    public async Task ReorderAsync_SetsRanks_AndReturnsOnlyChangedDevices()
    {
        var a = SeedDevice(rank: 0);
        var b = SeedDevice(rank: 1);

        var changed = (await _repo.ReorderAsync(
            new List<(Guid, int)> { (a, 1), (b, 0) }, WriteOrigin.Live)).ToList();

        changed.Should().HaveCount(2);
        changed.Should().Contain(d => d.Id == a && d.Rank == 1);
        changed.Should().Contain(d => d.Id == b && d.Rank == 0);
    }

    [Fact]
    public async Task ReorderAsync_SkipsUnchangedRanks()
    {
        var a = SeedDevice(rank: 0);
        var b = SeedDevice(rank: 1);

        // a keeps rank 0 (unchanged), b moves 1 → 2.
        var changed = (await _repo.ReorderAsync(
            new List<(Guid, int)> { (a, 0), (b, 2) }, WriteOrigin.Live)).ToList();

        changed.Should().ContainSingle();
        changed[0].Id.Should().Be(b);
        changed[0].Rank.Should().Be(2);
    }

    [Fact]
    public async Task ReorderAsync_IgnoresUnknownIds()
    {
        var a = SeedDevice(rank: 0);

        var changed = (await _repo.ReorderAsync(
            new List<(Guid, int)> { (a, 5), (Guid.NewGuid(), 9) }, WriteOrigin.Live)).ToList();

        changed.Should().ContainSingle();
        changed[0].Id.Should().Be(a);
        changed[0].Rank.Should().Be(5);
    }

    [Fact]
    public async Task ReorderAsync_EmptyList_ReturnsEmpty()
    {
        var changed = await _repo.ReorderAsync(new List<(Guid, int)>(), WriteOrigin.Live);
        changed.Should().BeEmpty();
    }

    [Fact]
    public async Task ReorderAsync_PersistsRanks()
    {
        var a = SeedDevice(rank: 0);

        await _repo.ReorderAsync(new List<(Guid, int)> { (a, 3) }, WriteOrigin.Live);

        var reloaded = await _repo.GetByIdAsync(a);
        reloaded!.Rank.Should().Be(3);
    }

    [Fact]
    public async Task GetAllAsync_OrdersByRank_SoReorderIsStable()
    {
        // Insertion order deliberately differs from rank order; unranked device sorts last.
        var high = SeedDevice(rank: 2);
        var top = SeedDevice(rank: 0);
        var unranked = SeedDevice(rank: null);

        var ordered = (await _repo.GetAllAsync()).Select(d => d.Id).ToList();

        ordered.Should().Equal(top, high, unranked);
    }
}
