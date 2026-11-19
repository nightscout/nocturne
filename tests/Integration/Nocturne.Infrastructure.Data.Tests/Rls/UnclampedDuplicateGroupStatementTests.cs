using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The reach of a delete or restore to the copies a caller's history clamp hides costs a caller with no
/// clamp nothing: the statements it runs are those it ran before that reach existed, and none of them
/// touches <c>app.history_clamped</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class UnclampedDuplicateGroupStatementTests : IAsyncLifetime
{
    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public UnclampedDuplicateGroupStatementTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task SingleDelete_RunsTheStatementsItRanBefore()
    {
        var (recent, _) = await SeedNoteGroupAsync();
        await using var store = await OpenAsync();
        var note = await store.Context.Notes.SingleAsync(n => n.Id == recent);
        store.Recorder.Clear();

        await DuplicateGroupPrimaries.SoftDeleteAsync(store.Context, note, RecordType.Note, default);

        store.Recorder.All.Should().HaveCount(
            4, "two reads of the group's links, one of its copies, then one batch that updates the row and its copy");
        store.ShouldNotTouchTheClamp();
    }

    [Fact]
    public async Task Restore_RunsTheStatementsItRanBefore()
    {
        var (recent, _) = await SeedNoteGroupAsync();
        await using (var delete = await OpenAsync())
        {
            var note = await delete.Context.Notes.SingleAsync(n => n.Id == recent);
            await DuplicateGroupPrimaries.SoftDeleteAsync(delete.Context, note, RecordType.Note, default);
        }

        await using var store = await OpenAsync();
        await store.Context.RestoreDeletedGroupAsync<NoteEntity>(recent, nameof(Note));

        store.Recorder.All.Should().HaveCount(
            11,
            "the row, the group lock, two reads of links, the deleted copies, the key check, one batch of updates, "
            + "then the repick's link read, lock, primaries and live rows");
        store.ShouldNotTouchTheClamp();
    }

    [Fact]
    public async Task BulkDeleteOfEveryCopy_RunsTheStatementsItRanBefore()
    {
        var (recent, _) = await SeedNoteGroupAsync();
        await using var store = await OpenAsync();

        await store.Context.AuditedSoftDeleteWithEntitiesAsync(
            store.Context.Notes.Where(n => n.Id == recent), new Mock<IAuditContext>().Object, "test",
            duplicates: DuplicateDelete.EveryCopy);

        store.Recorder.All.Should().HaveCount(
            8, "the stamp, the matched ids, two reads of links, the rows, their audit rows, the ids to write, one update");
        store.ShouldNotTouchTheClamp();
    }

    /// <summary>A note and its copy, both an hour old, in one duplicate group.</summary>
    private async Task<(Guid Recent, Guid Copy)> SeedNoteGroupAsync()
    {
        var now = DateTime.UtcNow;
        var recent = Guid.NewGuid();
        var copy = Guid.NewGuid();
        await using var seed = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        seed.Notes.AddRange(
            new NoteEntity { Id = recent, Timestamp = now.AddHours(-1), Text = "recent" },
            new NoteEntity
            {
                Id = copy, Timestamp = now.AddHours(-1), Text = "copy", DataSource = "pump", SyncIdentifier = copy.ToString(),
            });
        await DuplicateGroups.LinkAsync(seed, RecordType.Note, recent, copy);
        return (recent, copy);
    }

    /// <summary>
    /// A context on the app role with the tenant set and no clamp, as an owner's request opens one,
    /// recording every statement it runs.
    /// </summary>
    private async Task<Store> OpenAsync()
    {
        var conn = await _fx.OpenAppConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
            cmd.Parameters.AddWithValue("tid", _tenant.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        var recorder = new StatementRecorder();
        var context = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(recorder)
            .Options) { TenantId = _tenant };
        return new Store(conn, context, recorder);
    }

    private sealed class Store(NpgsqlConnection connection, NocturneDbContext context, StatementRecorder recorder)
        : IAsyncDisposable
    {
        public NocturneDbContext Context { get; } = context;
        public StatementRecorder Recorder { get; } = recorder;

        public void ShouldNotTouchTheClamp() =>
            Recorder.All.Should().NotContain(s => s.Contains("history_clamped"));

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
