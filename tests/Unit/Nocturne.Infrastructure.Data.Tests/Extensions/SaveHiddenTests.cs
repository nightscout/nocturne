using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// <see cref="DuplicateGroupPrimaries.SaveHiddenAsync{TEntity}"/> writes the hidden copies and nothing
/// else the context holds, and leaves none of them tracked. SQLite has no clamp, so the lift itself is
/// proven on Postgres, by the clamped delete and restore in <c>ClampedDuplicateGroupDeleteTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class SaveHiddenTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task WithAnotherChangePending_SavesNothing()
    {
        var (hidden, other) = await SeedAsync();
        await using var context = _db.CreateContext();
        var hiddenRow = await context.Notes.SingleAsync(n => n.Id == hidden);
        (await context.Notes.SingleAsync(n => n.Id == other)).Text = "changed";

        var save = () => DuplicateGroupPrimaries.SaveHiddenAsync(
            context, [hiddenRow], n => n.DeletedAt = DateTime.UtcNow, default);

        await save.Should().ThrowAsync<InvalidOperationException>();
        await using var verify = _db.CreateContext();
        (await verify.Notes.IncludingDeleted().CountAsync(n => n.DeletedAt != null)).Should().Be(0);
    }

    [Fact]
    public async Task WithNoOtherChangePending_SavesTheHiddenRows_AndStopsTrackingThem()
    {
        var (hidden, _) = await SeedAsync();
        await using var context = _db.CreateContext();
        var hiddenRow = await context.Notes.SingleAsync(n => n.Id == hidden);

        await DuplicateGroupPrimaries.SaveHiddenAsync(context, [hiddenRow], n => n.DeletedAt = DateTime.UtcNow, default);

        context.Entry(hiddenRow).State.Should().Be(EntityState.Detached);
        await using var verify = _db.CreateContext();
        (await verify.Notes.IncludingDeleted().Where(n => n.DeletedAt != null).Select(n => n.Id).ToListAsync())
            .Should().Equal(hidden);
    }

    private async Task<(Guid Hidden, Guid Other)> SeedAsync()
    {
        var hidden = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await using var seeding = _db.CreateContext();
        seeding.Notes.AddRange(
            new NoteEntity { Id = hidden, TenantId = TenantId, Text = "hidden" },
            new NoteEntity { Id = other, TenantId = TenantId, Text = "other" });
        await seeding.SaveChangesAsync();
        return (hidden, other);
    }
}
