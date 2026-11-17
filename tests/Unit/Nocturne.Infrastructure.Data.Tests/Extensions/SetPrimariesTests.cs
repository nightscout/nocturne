using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// <see cref="DuplicateGroupPrimaries.SetPrimariesAsync"/> is the only writer of the duplicate flag,
/// and the model never saves the flag itself.
/// </summary>
[Trait("Category", "Unit")]
public class SetPrimariesTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task MovesThePrimaryAndTheFlags_SoftDeletedRowsIncluded()
    {
        var (primary, copy) = await SeedPairAsync();
        await using (var deleting = _db.CreateContext())
        {
            await deleting.Notes.Where(n => n.Id == primary)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.DeletedAt, DateTime.UtcNow));
        }

        await using var context = _db.CreateContext();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await DuplicateGroupPrimaries.SetPrimariesAsync(
                context, RecordType.Note, [new PrimaryChange(primary, false), new PrimaryChange(copy, true)], default);
            await transaction.CommitAsync();
        }

        await using var verify = _db.CreateContext();
        (await verify.LinkedRecords.SingleAsync(l => l.IsPrimary)).RecordId.Should().Be(copy);
        (await FlaggedAsync(verify)).Should().Equal(primary);
    }

    [Fact]
    public async Task TheFlagFollowsTheLinkRatherThanThePlan()
    {
        var (_, copy) = await SeedPairAsync();
        var unlinked = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Text = "x", DataSource = "c" };
        await using (var seeding = _db.CreateContext())
        {
            seeding.Notes.Add(unlinked);
            await seeding.SaveChangesAsync();
            await seeding.Notes.Where(n => n.Id == copy).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsDuplicate, true));
        }

        await using var context = _db.CreateContext();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await DuplicateGroupPrimaries.SetPrimariesAsync(
                context, RecordType.Note, [new PrimaryChange(unlinked.Id, false), new PrimaryChange(copy, true)], default);
            await transaction.CommitAsync();
        }

        await using var verify = _db.CreateContext();
        (await FlaggedAsync(verify)).Should().BeEmpty(
            "the unlinked note has no non-primary link to hide it, and the copy's link was already primary");
    }

    [Fact]
    public async Task RefusesToRunOutsideATransaction()
    {
        var (primary, _) = await SeedPairAsync();
        await using var context = _db.CreateContext();

        var act = () => DuplicateGroupPrimaries.SetPrimariesAsync(
            context, RecordType.Note, [new PrimaryChange(primary, false)], default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ATrackedSaveNeverWritesTheFlag()
    {
        var (_, copy) = await SeedPairAsync();
        await using (var context = _db.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await DuplicateGroupPrimaries.SetPrimariesAsync(
                context, RecordType.Note, [new PrimaryChange(copy, false)], default);
            await transaction.CommitAsync();
        }

        var inserted = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Text = "new", IsDuplicate = true };
        await using (var context = _db.CreateContext())
        {
            context.Notes.Add(inserted);
            var stored = await context.Notes.SingleAsync(n => n.Id == copy);
            stored.IsDuplicate = false;
            stored.Text = "re-sent";
            await context.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        (await FlaggedAsync(verify)).Should().Equal(copy);
        (await verify.Notes.SingleAsync(n => n.Id == copy)).Text.Should().Be("re-sent");
    }

    private static Task<List<Guid>> FlaggedAsync(NocturneDbContext context) =>
        context.Notes.IgnoreQueryFilters().Where(n => n.IsDuplicate).Select(n => n.Id).ToListAsync();

    private async Task<(Guid Primary, Guid Copy)> SeedPairAsync()
    {
        await using var context = _db.CreateContext();
        var canonical = Guid.CreateVersion7();
        var primary = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Text = "x", DataSource = "a" };
        var copy = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Text = "x", DataSource = "b" };
        context.Notes.AddRange(primary, copy);
        context.LinkedRecords.AddRange(
            Link(canonical, primary.Id, isPrimary: true),
            Link(canonical, copy.Id, isPrimary: true));
        await context.SaveChangesAsync();
        return (primary.Id, copy.Id);
    }

    private static LinkedRecordEntity Link(Guid canonical, Guid recordId, bool isPrimary) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = TenantId, CanonicalId = canonical,
        RecordType = RecordTypeKeys.Key(RecordType.Note), RecordId = recordId, DataSource = "a", IsPrimary = isPrimary,
    };
}
