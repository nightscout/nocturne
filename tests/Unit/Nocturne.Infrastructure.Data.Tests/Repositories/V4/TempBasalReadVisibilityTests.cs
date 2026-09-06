using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>Holds the shared read-visibility predicate translatable by a real relational provider.</summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "TempBasal")]
public class TempBasalReadVisibilityTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TempBasalRepository _repo;

    public TempBasalReadVisibilityTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();

        _repo = new TempBasalRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<TempBasalRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetAsync_ExcludesNonPrimaryButKeepsPrimary()
    {
        var start = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        var primary = await _repo.CreateAsync(new TempBasal
        {
            StartTimestamp = start,
            EndTimestamp = start.AddMinutes(30),
            DataSource = "mylife-connector",
            Rate = 0.5,
        }, WriteOrigin.Live);
        var duplicate = await _repo.CreateAsync(new TempBasal
        {
            StartTimestamp = start,
            EndTimestamp = start.AddMinutes(30),
            DataSource = "glooko-connector",
            Rate = 0.5,
        }, WriteOrigin.Live);

        var canonicalId = Guid.CreateVersion7();
        var mills = new DateTimeOffset(start, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _context.LinkedRecords.AddRange(
            new LinkedRecordEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TestTenantId,
                CanonicalId = canonicalId,
                RecordType = "tempbasal",
                RecordId = primary.Id,
                SourceTimestamp = mills,
                DataSource = "mylife-connector",
                IsPrimary = true,
            },
            new LinkedRecordEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TestTenantId,
                CanonicalId = canonicalId,
                RecordType = "tempbasal",
                RecordId = duplicate.Id,
                SourceTimestamp = mills,
                DataSource = "glooko-connector",
                IsPrimary = false,
            });
        await _context.SaveChangesAsync();

        var fetched = (await _repo.GetAsync(
            from: null, to: null, device: null, source: null)).ToList();

        fetched.Should().ContainSingle().Which.Id.Should().Be(primary.Id);
    }
}
