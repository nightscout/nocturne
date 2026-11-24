using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A user's delete or restore of one record reaches every copy in its duplicate group, however the
/// caller's 24 hour clamp narrows its reads, and gives back or broadcasts only the copies the clamp
/// shows that caller.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ClampedDuplicateGroupDeleteTests : IAsyncLifetime
{
    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public ClampedDuplicateGroupDeleteTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task ClampedSpanDelete_DeletesACopyThatEndedBeforeTheWindow_AndReturnsOnlyTheVisibleSpan()
    {
        var (running, ended) = await SeedSpanGroupAsync();

        await using (var scope = OpenScope(clamped: true))
        {
            var deleted = await SpanRepository(scope).DeleteStateSpanAsync(running.ToString());
            deleted.Select(s => s.Id).Should().Equal(running.ToString());
        }

        (await DeletedSpansAsync(running, ended)).Should().BeEquivalentTo([running, ended]);
    }

    [Fact]
    public async Task ClampedSpanRestore_RestoresTheHiddenCopy_AndReturnsOnlyTheVisibleSpan()
    {
        var (running, ended) = await SeedSpanGroupAsync();
        await using (var scope = OpenScope(clamped: false))
            await SpanRepository(scope).DeleteStateSpanAsync(running.ToString());

        await using (var scope = OpenScope(clamped: true))
        {
            await using var db = await Contexts(scope).CreateAsync();
            var restore = await db.RestoreDeletedGroupAsync<StateSpanEntity>(running, nameof(StateSpan));

            restore.Restored.Select(s => s.Id).Should().Equal(running);
            restore.Copies.Should().BeEmpty();
            restore.Promoted.Should().BeEmpty();
        }

        (await DeletedSpansAsync(running, ended)).Should().BeEmpty();
    }

    [Fact]
    public async Task ClampedSpanDelete_OfASpanTheClampHides_FindsNothing()
    {
        var (running, ended) = await SeedSpanGroupAsync();

        await using (var scope = OpenScope(clamped: true))
            (await SpanRepository(scope).DeleteStateSpanAsync(ended.ToString())).Should().BeEmpty();

        (await DeletedSpansAsync(running, ended)).Should().BeEmpty();
    }

    [Fact]
    public async Task UnclampedSpanDeleteAndRestore_ReachAndReturnEveryCopy()
    {
        var (running, ended) = await SeedSpanGroupAsync();

        await using (var scope = OpenScope(clamped: false))
        {
            var deleted = await SpanRepository(scope).DeleteStateSpanAsync(running.ToString());
            deleted.Select(s => s.Id).Should().Equal(running.ToString(), ended.ToString());
        }

        (await DeletedSpansAsync(running, ended)).Should().BeEquivalentTo([running, ended]);

        await using (var scope = OpenScope(clamped: false))
        {
            await using var db = await Contexts(scope).CreateAsync();
            var restore = await db.RestoreDeletedGroupAsync<StateSpanEntity>(running, nameof(StateSpan));
            restore.Copies.Select(s => s.Id).Should().Equal(ended);
        }

        (await DeletedSpansAsync(running, ended)).Should().BeEmpty();
    }

    [Fact]
    public async Task ClampedNoteDeleteAndRestore_ReachTheCopyBeforeTheWindow_AndBroadcastOnlyTheVisibleNote()
    {
        var (recent, old) = await SeedNoteGroupAsync();
        var broadcaster = new RecordingV4RecordBroadcaster<Note>();

        await using (var scope = OpenScope(clamped: true))
            await NoteRepository(scope, broadcaster).DeleteAsync(recent, WriteOrigin.Live);

        broadcaster.Deleted.Should().Equal(recent);
        (await DeletedNotesAsync(recent, old)).Should().BeEquivalentTo([recent, old]);

        await using (var scope = OpenScope(clamped: true))
        {
            var restored = await NoteRepository(scope, broadcaster).RestoreAsync(recent, WriteOrigin.Live);
            restored.Id.Should().Be(recent);
        }

        broadcaster.Created.Select(n => n.Id).Should().Equal(recent);
        broadcaster.Updated.Should().BeEmpty();
        (await DeletedNotesAsync(recent, old)).Should().BeEmpty();
    }

    [Fact]
    public async Task ClampedNoteRestore_OfAGroupWhosePrimaryIsBeforeTheWindow_RestoresIt_AndReturnsNothingHidden()
    {
        var (recent, old) = await SeedNoteGroupAsync(oldIsPrimary: true);
        var broadcaster = new RecordingV4RecordBroadcaster<Note>();
        await using (var scope = OpenScope(clamped: true))
            await NoteRepository(scope, broadcaster).DeleteAsync(recent, WriteOrigin.Live);
        (await DeletedNotesAsync(recent, old)).Should().BeEquivalentTo([recent, old]);

        await using (var scope = OpenScope(clamped: true))
        {
            await using var db = await Contexts(scope).CreateAsync();
            var restore = await db.RestoreDeletedGroupAsync<NoteEntity>(recent, nameof(Note));
            restore.Copies.Should().BeEmpty();
            restore.Promoted.Should().BeEmpty();
        }

        (await DeletedNotesAsync(recent, old)).Should().BeEmpty();
        await using var read = await Contexts(_provider).CreateForTenantAsync(_tenant);
        (await read.LinkedRecords.AsNoTracking().SingleAsync(l => l.RecordId == old)).IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task ClampedNoteBulkDelete_DeletesTheCopyBeforeTheWindow_AndReturnsOnlyTheVisibleNote()
    {
        var (recent, old) = await SeedNoteGroupAsync();

        await using (var scope = OpenScope(clamped: true))
        {
            await using var db = await Contexts(scope).CreateAsync();
            var result = await db.AuditedSoftDeleteWithEntitiesAsync(
                db.Notes.Where(n => n.Id == recent), new Mock<IAuditContext>().Object, "test",
                duplicates: DuplicateDelete.EveryCopy);

            result.Count.Should().Be(1);
            result.Entities.Select(n => n.Id).Should().Equal(recent);
        }

        (await DeletedNotesAsync(recent, old)).Should().BeEquivalentTo([recent, old]);
    }

    [Fact]
    public async Task ClampedNoteBulkDelete_AuditsTheHiddenCopy_AsTheCaller()
    {
        var (recent, old) = await SeedNoteGroupAsync();
        var actor = Actor();

        await using (var scope = OpenScope(clamped: true))
        {
            await using var db = await Contexts(scope).CreateAsync();
            await db.AuditedSoftDeleteWithEntitiesAsync(
                db.Notes.Where(n => n.Id == recent), actor, "test", duplicates: DuplicateDelete.EveryCopy);
        }

        var audit = await AuditRowsAsync(actor);
        audit.Select(a => (a.EntityId, a.Action)).Should().BeEquivalentTo([(recent, "delete"), (old, "delete")]);
        audit.Single(a => a.EntityId == old).ChangesJson.Should().Contain("\"old\"");
    }

    [Fact]
    public async Task ClampedNoteSummaryDelete_CountsTheHiddenCopyInTheAuditOnly()
    {
        var (recent, _) = await SeedNoteGroupAsync();
        var actor = Actor();

        await using (var scope = OpenScope(clamped: true))
        {
            await using var db = await Contexts(scope).CreateAsync();
            var count = await db.AuditedSoftDeleteAsync(
                db.Notes.Where(n => n.Id == recent), actor, "test", duplicates: DuplicateDelete.EveryCopy);
            count.Should().Be(1);
        }

        var summary = (await AuditRowsAsync(actor)).Should().ContainSingle().Subject;
        summary.Action.Should().Be("bulk_delete");
        JsonDocument.Parse(summary.ChangesJson!).RootElement.GetProperty("count").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task UnclampedNoteBulkDelete_ReturnsEveryCopy()
    {
        var (recent, old) = await SeedNoteGroupAsync();

        await using (var scope = OpenScope(clamped: false))
        {
            await using var db = await Contexts(scope).CreateAsync();
            var result = await db.AuditedSoftDeleteWithEntitiesAsync(
                db.Notes.Where(n => n.Id == recent), new Mock<IAuditContext>().Object, "test",
                duplicates: DuplicateDelete.EveryCopy);

            result.Count.Should().Be(2);
            result.Entities.Select(n => n.Id).Should().BeEquivalentTo([recent, old]);
        }

        (await DeletedNotesAsync(recent, old)).Should().BeEquivalentTo([recent, old]);
    }

    [Fact]
    public async Task ClampedNoteRestore_LeavesDeletedAHiddenCopyAnotherDeleteTook()
    {
        var (recent, old) = await SeedNoteGroupAsync();
        await using (var scope = OpenScope(clamped: false))
            await NoteRepository(scope, new RecordingV4RecordBroadcaster<Note>()).DeleteAsync(old, WriteOrigin.Live);
        await using (var revive = await Contexts(_provider).CreateForTenantAsync(_tenant))
            await revive.Notes.IncludingDeleted().Where(n => n.Id == recent)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.DeletedAt, (DateTime?)null));
        await using (var scope = OpenScope(clamped: true))
            await NoteRepository(scope, new RecordingV4RecordBroadcaster<Note>()).DeleteAsync(recent, WriteOrigin.Live);

        await using (var scope = OpenScope(clamped: true))
            await NoteRepository(scope, new RecordingV4RecordBroadcaster<Note>()).RestoreAsync(recent, WriteOrigin.Live);

        (await DeletedNotesAsync(recent, old)).Should().Equal(old);
    }

    [Fact]
    public async Task ClampedNoteRestore_LeavesDeletedAHiddenCopyWhoseKeyAHiddenLiveRowHolds()
    {
        var (recent, old) = await SeedNoteGroupAsync();
        await using (var scope = OpenScope(clamped: true))
            await NoteRepository(scope, new RecordingV4RecordBroadcaster<Note>()).DeleteAsync(recent, WriteOrigin.Live);
        var holder = Guid.NewGuid();
        await using (var seed = await Contexts(_provider).CreateForTenantAsync(_tenant))
        {
            seed.Notes.Add(new NoteEntity
            {
                Id = holder, Timestamp = DateTime.UtcNow.AddHours(-40), Text = "holder",
                DataSource = "pump", SyncIdentifier = old.ToString(),
            });
            await seed.SaveChangesAsync();
        }

        await using (var scope = OpenScope(clamped: true))
            (await NoteRepository(scope, new RecordingV4RecordBroadcaster<Note>()).RestoreAsync(recent, WriteOrigin.Live))
                .Id.Should().Be(recent);

        (await DeletedNotesAsync(recent, old, holder)).Should().Equal(old);
    }

    /// <summary>
    /// An ongoing span, its group's primary, and a copy that ended two days ago, which the clamp hides
    /// however recent the group's primary is.
    /// </summary>
    private async Task<(Guid Running, Guid Ended)> SeedSpanGroupAsync()
    {
        var now = DateTime.UtcNow;
        var running = Guid.NewGuid();
        var ended = Guid.NewGuid();
        await using var seed = await Contexts(_provider).CreateForTenantAsync(_tenant);
        seed.StateSpans.AddRange(
            new StateSpanEntity { Id = running, Category = "Override", State = "running", StartTimestamp = now.AddDays(-3) },
            new StateSpanEntity
            {
                Id = ended, Category = "Override", State = "ended",
                StartTimestamp = now.AddDays(-3), EndTimestamp = now.AddDays(-2),
            });
        await DuplicateGroups.LinkAsync(seed, RecordType.StateSpan, running, ended);
        return (running, ended);
    }

    /// <summary>A note an hour old and its copy 30 hours old, across the edge of the window.</summary>
    private async Task<(Guid Recent, Guid Old)> SeedNoteGroupAsync(bool oldIsPrimary = false)
    {
        var now = DateTime.UtcNow;
        var recent = Guid.NewGuid();
        var old = Guid.NewGuid();
        await using var seed = await Contexts(_provider).CreateForTenantAsync(_tenant);
        seed.Notes.AddRange(
            new NoteEntity { Id = recent, Timestamp = now.AddHours(-1), Text = "recent" },
            new NoteEntity
            {
                Id = old, Timestamp = now.AddHours(-30), Text = "old", DataSource = "pump", SyncIdentifier = old.ToString(),
            });
        if (oldIsPrimary)
            await DuplicateGroups.LinkAsync(seed, RecordType.Note, old, recent);
        else
            await DuplicateGroups.LinkAsync(seed, RecordType.Note, recent, old);
        return (recent, old);
    }

    private async Task<List<Guid>> DeletedSpansAsync(params Guid[] ids)
    {
        await using var read = await Contexts(_provider).CreateForTenantAsync(_tenant);
        return await read.StateSpans.IncludingDeleted().AsNoTracking()
            .Where(s => ids.Contains(s.Id) && s.DeletedAt != null).Select(s => s.Id).ToListAsync();
    }

    private async Task<List<Guid>> DeletedNotesAsync(params Guid[] ids)
    {
        await using var read = await Contexts(_provider).CreateForTenantAsync(_tenant);
        return await read.Notes.IncludingDeleted().AsNoTracking()
            .Where(n => ids.Contains(n.Id) && n.DeletedAt != null).Select(n => n.Id).ToListAsync();
    }

    private static IAuditContext Actor()
    {
        var actor = new Mock<IAuditContext>();
        actor.SetupGet(a => a.SubjectId).Returns(Guid.NewGuid());
        return actor.Object;
    }

    private async Task<List<MutationAuditLogEntity>> AuditRowsAsync(IAuditContext actor)
    {
        await using var read = await Contexts(_provider).CreateForTenantAsync(_tenant);
        return await read.MutationAuditLog.AsNoTracking()
            .Where(a => a.ActorSubjectId == actor.SubjectId).ToListAsync();
    }

    private AsyncServiceScope OpenScope(bool clamped)
    {
        var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        if (clamped)
            scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        return scope;
    }

    private static INocturneDbContextFactory Contexts(IServiceProvider provider) =>
        provider.GetRequiredService<INocturneDbContextFactory>();

    private static INocturneDbContextFactory Contexts(AsyncServiceScope scope) => Contexts(scope.ServiceProvider);

    private static StateSpanRepository SpanRepository(AsyncServiceScope scope) => new(
        Contexts(scope),
        new Mock<IDuplicateLinker>().Object,
        new Mock<IAuditContext>().Object,
        NullLogger<StateSpanRepository>.Instance);

    private static NoteRepository NoteRepository(AsyncServiceScope scope, RecordingV4RecordBroadcaster<Note> broadcaster) => new(
        Contexts(scope),
        new Mock<IDuplicateLinker>().Object,
        new Mock<IAuditContext>().Object,
        NullLogger<NoteRepository>.Instance,
        broadcaster);
}
