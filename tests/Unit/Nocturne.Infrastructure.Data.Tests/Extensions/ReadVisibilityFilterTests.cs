using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// <see cref="ReadVisibilityFilter"/> shows a duplicate group once, by the flag the deduplication
/// module keeps, and a v3 history read still delivers a duplicate's delete.
/// </summary>
[Trait("Category", "Unit")]
public class ReadVisibilityFilterTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime At = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExcludeDuplicates_ShowsAGroupThroughItsPrimary()
    {
        var (primary, _) = await SeedGroupAsync();

        await using var context = _db.CreateContext();
        (await context.Notes.ExcludeDuplicates().Select(n => n.Id).ToListAsync()).Should().Equal(primary);
        (await context.Notes.ExcludeDuplicatesWhereFlagged().Select(n => n.Id).ToListAsync()).Should().Equal(primary);
    }

    [Fact]
    public async Task KeepingDeleted_DeliversADeletedDuplicate_AndHidesALiveOne()
    {
        var (primary, copy) = await SeedGroupAsync();
        var (otherPrimary, deletedCopy) = await SeedGroupAsync(At.AddHours(1));
        await using (var deleting = _db.CreateContext())
            await deleting.Notes.Where(n => n.Id == deletedCopy)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.DeletedAt, At.AddHours(2)));

        await using var context = _db.CreateContext();
        Guid[] expected = [primary, otherPrimary, deletedCopy];
        (await context.Notes.IncludingDeleted().ExcludeDuplicatesKeepingDeleted().Select(n => n.Id).ToListAsync())
            .Should().BeEquivalentTo(expected).And.NotContain(copy);
        (await context.Notes.IncludingDeleted().ExcludeDuplicatesWhereFlaggedKeepingDeleted().Select(n => n.Id).ToListAsync())
            .Should().BeEquivalentTo(expected);

        var history = await new NoteRepository(
                context.AsFactory(), Mock.Of<IDeduplicationService>(), Mock.Of<IAuditContext>(),
                NullLogger<NoteRepository>.Instance)
            .GetModifiedSinceAsync(0, 100);
        history.Select(h => (h.Record.Id, h.Deleted)).Should().BeEquivalentTo(
            [(primary, false), (otherPrimary, false), (deletedCopy, true)]);
    }

    [Fact]
    public async Task WhereFlagged_LeavesATypeDeduplicationDoesNotLinkUnfiltered()
    {
        await using (var seeding = _db.CreateContext())
        {
            seeding.MeterGlucose.Add(new MeterGlucoseEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = At, Mgdl = 100 });
            await seeding.SaveChangesAsync();
        }

        await using var context = _db.CreateContext();
        (await context.MeterGlucose.ExcludeDuplicatesWhereFlagged().CountAsync()).Should().Be(1);
    }

    private async Task<(Guid Primary, Guid Copy)> SeedGroupAsync(DateTime? at = null)
    {
        await using var context = _db.CreateContext();
        var primary = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = at ?? At, Text = "x", DataSource = "a" };
        var copy = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = at ?? At, Text = "x", DataSource = "b" };
        context.Notes.AddRange(primary, copy);
        await DuplicateGroups.LinkAsync(context, RecordType.Note, primary.Id, copy.Id);
        return (primary.Id, copy.Id);
    }
}
