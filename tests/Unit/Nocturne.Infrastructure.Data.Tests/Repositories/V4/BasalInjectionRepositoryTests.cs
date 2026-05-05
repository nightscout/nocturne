using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "BasalInjection")]
public class BasalInjectionRepositoryTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly NocturneDbContext _context;
    private readonly BasalInjectionRepository _repo;

    public BasalInjectionRepositoryTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using (var seedContext = new NocturneDbContext(_contextOptions))
        {
            seedContext.TenantId = TestTenantId;
            seedContext.Database.EnsureCreated();
            seedContext.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
            seedContext.SaveChanges();
        }

        _context = new NocturneDbContext(_contextOptions);
        _context.TenantId = TestTenantId;

        _repo = new BasalInjectionRepository(
            _context,
            new Mock<IAuditContext>().Object,
            NullLogger<BasalInjectionRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static BasalInjection MakeInjection(string dataSource, string syncIdentifier, double units = 10.0)
    {
        return new BasalInjection
        {
            Timestamp = DateTime.UtcNow,
            DataSource = dataSource,
            SyncIdentifier = syncIdentifier,
            Units = units,
            InsulinContext = new TreatmentInsulinContext
            {
                PatientInsulinId = Guid.NewGuid(),
                InsulinName = "Tresiba",
                Dia = 24.0,
                Peak = 720,
                Curve = "long-acting",
            },
        };
    }

    [Fact]
    public async Task FindBySyncIdentifierAsync_returns_live_row_when_present()
    {
        var created = await _repo.CreateAsync(MakeInjection("aaps", "sync-1"));

        var found = await _repo.FindBySyncIdentifierAsync("aaps", "sync-1");

        found.Should().NotBeNull();
        found!.Id.Should().Be(created.Id);
        found.DataSource.Should().Be("aaps");
        found.SyncIdentifier.Should().Be("sync-1");
    }

    [Fact]
    public async Task FindBySyncIdentifierAsync_returns_null_for_soft_deleted_row()
    {
        var created = await _repo.CreateAsync(MakeInjection("aaps", "sync-2"));

        await _repo.DeleteAsync(created.Id);

        var found = await _repo.FindBySyncIdentifierAsync("aaps", "sync-2");
        found.Should().BeNull();
    }

    // The end-to-end "soft-delete writes a MutationAuditLogEntity 'delete' entry"
    // assertion is intentionally NOT made here. The audit interceptor lives outside
    // the repository and is wired only by the production composition root, so a
    // unit-test fixture cannot exercise it without duplicating that wiring. The
    // assertion belongs at integration-test level where the full interceptor stack
    // is live (Phase 3 / Task 3.3 BasalInjectionIntegrationTests).
    [Fact]
    public async Task DeleteAsync_sets_DeletedAt()
    {
        var created = await _repo.CreateAsync(MakeInjection("aaps", "sync-3"));

        await _repo.DeleteAsync(created.Id);

        // Normal queries (with global filter) must not return the row.
        var visible = await _context.BasalInjections.FirstOrDefaultAsync(e => e.Id == created.Id);
        visible.Should().BeNull();

        // Bypassing the global filter, the row remains with DeletedAt set.
        var raw = await _context.BasalInjections
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == created.Id);
        raw.Should().NotBeNull();
        raw!.DeletedAt.Should().NotBeNull();
    }
}
