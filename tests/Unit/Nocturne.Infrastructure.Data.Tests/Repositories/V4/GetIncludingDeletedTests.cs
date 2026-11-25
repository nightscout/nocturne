using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// <see cref="BolusRepository.GetIncludingDeletedAsync"/> lists the window the way
/// <c>GetAsync</c> does, with the rows stored deleted in it: one row per dose, in observation order.
/// </summary>
[Trait("Category", "Unit")]
public class GetIncludingDeletedTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Start = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly BolusRepository _repository;

    public GetIncludingDeletedTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
        _context.TenantId = TenantId;
        _repository = new BolusRepository(
            new TestNocturneDbContextFactory(_context),
            new Mock<IDuplicateLinker>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<BolusRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<BolusEntity> AddAsync(int minute, bool deleted = false)
    {
        var entity = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Timestamp = Start.AddMinutes(minute),
            Insulin = 1.0,
            DeletedAt = deleted ? Start.AddHours(1) : null,
        };
        _context.Boluses.Add(entity);
        await _context.SaveChangesAsync();
        return entity;
    }

    [Fact]
    public async Task ListsLiveAndDeletedRowsInTheWindow_BoundsInclusive_DeletedFlagged()
    {
        await AddAsync(-1);
        var first = await AddAsync(0, deleted: true);
        var live = await AddAsync(5);
        var last = await AddAsync(10);
        await AddAsync(11, deleted: true);

        var rows = await _repository.GetIncludingDeletedAsync(
            Start, Start.AddMinutes(10), limit: 10, descending: false);

        rows.Select(r => (r.Record.Id, r.Deleted)).Should().Equal(
            (first.Id, true), (live.Id, false), (last.Id, false));
    }

    [Fact]
    public async Task LimitAndOrderApplyToLiveAndDeletedRowsTogether()
    {
        var oldest = await AddAsync(0);
        var middle = await AddAsync(1, deleted: true);
        var newest = await AddAsync(2, deleted: true);

        var newestFirst = await _repository.GetIncludingDeletedAsync(null, null, limit: 2, descending: true);
        var oldestFirst = await _repository.GetIncludingDeletedAsync(null, null, limit: 2, descending: false);

        newestFirst.Select(r => r.Record.Id).Should().Equal(newest.Id, middle.Id);
        oldestFirst.Select(r => r.Record.Id).Should().Equal(oldest.Id, middle.Id);
    }

    [Fact]
    public async Task ADeletedCopyAnotherCopyStandsFor_IsNotListedWhileThePrimaryIs()
    {
        var primary = await AddAsync(0);
        var deletedCopy = await AddAsync(0, deleted: true);
        var liveCopy = await AddAsync(0);
        await DuplicateGroups.LinkAsync(_context, RecordType.Bolus, primary.Id, deletedCopy.Id, liveCopy.Id);

        var rows = await _repository.GetIncludingDeletedAsync(null, null, limit: 10, descending: true);

        rows.Should().ContainSingle().Which.Record.Id.Should().Be(primary.Id);
    }

    [Fact]
    public async Task ADeletedPrimary_IsListedOnceAsDeleted()
    {
        var primary = await AddAsync(0, deleted: true);
        var deletedCopy = await AddAsync(0, deleted: true);
        await DuplicateGroups.LinkAsync(_context, RecordType.Bolus, primary.Id, deletedCopy.Id);

        var rows = await _repository.GetIncludingDeletedAsync(null, null, limit: 10, descending: true);

        var row = rows.Should().ContainSingle().Subject;
        (row.Record.Id, row.Deleted).Should().Be((primary.Id, true));
    }
}
