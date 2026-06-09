using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class TempBasalRepositoryBulkCreateTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly TempBasalRepository _repository;

    public TempBasalRepositoryBulkCreateTests()
    {
        var dbName = $"tempbasal_bulk_tests_{Guid.NewGuid()}";
        _context = TestDbContextFactory.CreateInMemoryContext(dbName);
        _context.TenantId = TenantA;
        _repository = new TempBasalRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<TempBasalRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TempBasal CreateRecord(string? legacyId = null, DateTime? start = null) =>
        new()
        {
            StartTimestamp = start ?? new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            UtcOffset = 0,
            Rate = 1.0,
            Origin = TempBasalOrigin.Manual,
            LegacyId = legacyId,
        };

    private void SoftDelete(Guid id)
    {
        var entity = _context.TempBasals.IgnoreQueryFilters().Single(e => e.Id == id);
        entity.DeletedAt = DateTime.UtcNow;
        _context.SaveChanges();
    }

    private void SeedDeleteAudit(Guid entityId, string? authType)
    {
        _context.MutationAuditLog.Add(new MutationAuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantA,
            EntityType = "TempBasal",
            EntityId = entityId,
            Action = "delete",
            AuthType = authType,
            CreatedAt = DateTime.UtcNow,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task BulkCreateAsync_ActiveLegacyId_Skipped()
    {
        await _repository.CreateAsync(CreateRecord("legacy-1"));

        var result = (await _repository.BulkCreateAsync([CreateRecord("legacy-1")])).ToList();

        result.Should().BeEmpty();
        _context.TempBasals.Count().Should().Be(1);
    }

    [Fact]
    public async Task BulkCreateAsync_SystemSoftDeleted_ReImports()
    {
        var existing = await _repository.CreateAsync(CreateRecord("legacy-1"));
        SoftDelete(existing.Id);
        SeedDeleteAudit(existing.Id, authType: null);

        var result = (await _repository.BulkCreateAsync([CreateRecord("legacy-1")])).ToList();

        result.Should().HaveCount(1);
        result[0].Id.Should().NotBe(existing.Id);
        _context.TempBasals.IgnoreQueryFilters().Count().Should().Be(2);
    }

    [Fact]
    public async Task BulkCreateAsync_UserSoftDeleted_DoesNotReImport()
    {
        var existing = await _repository.CreateAsync(CreateRecord("legacy-1"));
        SoftDelete(existing.Id);
        SeedDeleteAudit(existing.Id, authType: "OAuthAccessToken");

        var result = (await _repository.BulkCreateAsync([CreateRecord("legacy-1")])).ToList();

        result.Should().BeEmpty();
        _context.TempBasals.IgnoreQueryFilters().Count().Should().Be(1);
    }

    [Fact]
    public async Task BulkCreateAsync_PreAuditSoftDeleted_ReImports()
    {
        var existing = await _repository.CreateAsync(CreateRecord("legacy-1"));
        SoftDelete(existing.Id);
        // No audit row seeded — represents pre-audit legacy data.

        var result = (await _repository.BulkCreateAsync([CreateRecord("legacy-1")])).ToList();

        result.Should().HaveCount(1);
        _context.TempBasals.IgnoreQueryFilters().Count().Should().Be(2);
    }

    [Fact]
    public async Task BulkCreateAsync_NewLegacyId_Inserts()
    {
        var result = (await _repository.BulkCreateAsync([CreateRecord("legacy-new")])).ToList();

        result.Should().HaveCount(1);
        result[0].LegacyId.Should().Be("legacy-new");
        _context.TempBasals.Count().Should().Be(1);
    }
}
