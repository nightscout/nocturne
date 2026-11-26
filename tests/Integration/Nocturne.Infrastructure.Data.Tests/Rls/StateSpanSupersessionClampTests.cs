using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A soft delete undoes the supersession it gave spans the deleting member's 24 hour clamp hides, as the
/// foreign key's <c>SetNull</c> reached them, and ends them at successors the clamp hides. A restore ends
/// hidden spans at the restored span again. An upsert judges a span's end against superseders and
/// successors the clamp hides.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class StateSpanSupersessionClampTests : IAsyncLifetime
{
    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public StateSpanSupersessionClampTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task ClampedMemberDelete_ReopensAnOlderSpanTheClampHides()
    {
        var now = DateTime.UtcNow;
        var supersessor = Guid.NewGuid();
        var older = Guid.NewGuid();
        await SeedAsync(
            new StateSpanEntity
            {
                Id = older, Category = "TemporaryTarget", State = "older",
                StartTimestamp = now.AddDays(-5), EndTimestamp = now.AddDays(-3), SupersededById = supersessor,
            },
            new StateSpanEntity
            {
                Id = supersessor, Category = "TemporaryTarget", State = "open", StartTimestamp = now.AddDays(-3),
            });

        await DeleteAsClampedMemberAsync(supersessor);

        var reopened = await ReadAsync(older);
        reopened.EndTimestamp.Should().BeNull();
        reopened.SupersededById.Should().BeNull();
    }

    /// <summary>
    /// The successor is a snapshot of the deleted treatment's own override, which did not end it, and
    /// ended over a day ago, so the clamp hides it as well as the span it now ends.
    /// </summary>
    [Fact]
    public async Task ClampedMemberDelete_EndsAHiddenReopenedSpanAtASuccessorTheClampHides()
    {
        var now = DateTime.UtcNow;
        var supersessor = Guid.NewGuid();
        var older = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        await SeedAsync(
            new StateSpanEntity
            {
                Id = older, Category = "Override", State = "Custom",
                StartTimestamp = now.AddDays(-5), EndTimestamp = now.AddDays(-3), SupersededById = supersessor,
                MetadataJson = Metadata(StateSpanMetadataExtensions.TreatmentsCollection, "reason", "P Party"),
            },
            new StateSpanEntity
            {
                Id = supersessor, Category = "Override", State = "Custom", StartTimestamp = now.AddDays(-3),
                MetadataJson = Metadata(StateSpanMetadataExtensions.TreatmentsCollection, "reason", "N Night"),
            },
            new StateSpanEntity
            {
                Id = snapshot, Category = "Override", State = "Custom",
                StartTimestamp = now.AddDays(-2), EndTimestamp = now.AddDays(-1.5),
                MetadataJson = Metadata(StateSpanMetadataExtensions.DeviceStatusCollection, "name", "Night"),
            });

        await DeleteAsClampedMemberAsync(supersessor);

        var ended = await ReadAsync(older);
        ended.EndTimestamp.Should().BeCloseTo(now.AddDays(-2), TimeSpan.FromMilliseconds(1));
        ended.SupersededById.Should().Be(snapshot);
    }

    /// <summary>
    /// The older span ends at a span that ended over a day ago, so the clamp hides it before the restore
    /// and after it.
    /// </summary>
    [Fact]
    public async Task ClampedMemberRestore_EndsAnOlderSpanTheClampHidesAtTheRestoredSpan()
    {
        var now = DateTime.UtcNow;
        var older = Guid.NewGuid();
        var restored = Guid.NewGuid();
        var later = Guid.NewGuid();
        await SeedAsync(
            new StateSpanEntity
            {
                Id = later, Category = "TemporaryTarget", State = "later",
                StartTimestamp = now.AddDays(-2), EndTimestamp = now.AddDays(-1.9),
            },
            new StateSpanEntity
            {
                Id = older, Category = "TemporaryTarget", State = "older",
                StartTimestamp = now.AddDays(-5), EndTimestamp = now.AddDays(-2), SupersededById = later,
            },
            new StateSpanEntity
            {
                Id = restored, Category = "TemporaryTarget", State = "restored",
                StartTimestamp = now.AddDays(-3), DeletedAt = now.AddHours(-1),
            });

        StateSpan answered;
        await using (var scope = _provider.CreateAsyncScope())
            answered = await ClampedRepository(scope).RestoreAsync(restored, WriteOrigin.Live);

        var ended = await ReadAsync(older);
        ended.EndTimestamp.Should().BeCloseTo(now.AddDays(-3), TimeSpan.FromMilliseconds(1));
        ended.SupersededById.Should().Be(restored);
        answered.SupersededById.Should().Be(later.ToString());
        (await ReadAsync(restored)).SupersededById.Should().Be(later);
    }

    /// <summary>A re-sync of history sends the span with no end; both spans ended over a day ago.</summary>
    [Fact]
    public async Task ClampedWriterUpsert_OfAnOpenSpanTheClampHides_KeepsTheEndAHiddenSpanGave()
    {
        var now = DateTime.UtcNow;
        var older = Guid.NewGuid();
        var supersessor = Guid.NewGuid();
        await SeedAsync(
            Legacy(older, "tt-older", now.AddDays(-5), now.AddDays(-3), supersessor),
            Legacy(supersessor, "tt-supersessor", now.AddDays(-3), now.AddDays(-2.5), null));

        await using (var scope = _provider.CreateAsyncScope())
            await ClampedRepository(scope).BulkUpsertAsync([Sent("tt-older", now.AddDays(-5), null)]);

        var row = await ReadAsync(older);
        row.EndTimestamp.Should().BeCloseTo(now.AddDays(-3), TimeSpan.FromMilliseconds(1));
        row.SupersededById.Should().Be(supersessor);
    }

    /// <summary>
    /// The batch moves the hidden span that ended the older one, so the older span is left open and ended
    /// after the save at the moved span, which the clamp still hides.
    /// </summary>
    [Fact]
    public async Task ClampedWriterUpsert_ThatMovesAHiddenSupersessor_EndsTheOpenedSpanAtItsNewStart()
    {
        var now = DateTime.UtcNow;
        var older = Guid.NewGuid();
        var supersessor = Guid.NewGuid();
        await SeedAsync(
            Legacy(older, "tt-older", now.AddDays(-5), now.AddDays(-3), supersessor),
            Legacy(supersessor, "tt-supersessor", now.AddDays(-3), now.AddDays(-2), null));

        await using (var scope = _provider.CreateAsyncScope())
            await ClampedRepository(scope).BulkUpsertAsync(
            [
                Sent("tt-supersessor", now.AddDays(-2.5), now.AddDays(-2)),
                Sent("tt-older", now.AddDays(-5), null),
            ]);

        var row = await ReadAsync(older);
        row.EndTimestamp.Should().BeCloseTo(now.AddDays(-2.5), TimeSpan.FromMilliseconds(1));
        row.SupersededById.Should().Be(supersessor);
    }

    /// <summary>A backfill of an open span from five days ago; the span after it ended over two days ago.</summary>
    [Fact]
    public async Task ClampedWriterUpsert_OfAnOldOpenSpan_EndsItAtASuccessorTheClampHides()
    {
        var now = DateTime.UtcNow;
        var successor = Guid.NewGuid();
        await SeedAsync(Legacy(successor, "tt-successor", now.AddDays(-3), now.AddDays(-2.5), null));

        await using (var scope = _provider.CreateAsyncScope())
            await ClampedRepository(scope).BulkUpsertAsync([Sent("tt-backfill", now.AddDays(-5), null)]);

        await using var read = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        var row = await read.StateSpans.AsNoTracking().SingleAsync(s => s.LegacyId == "tt-backfill");
        row.EndTimestamp.Should().BeCloseTo(now.AddDays(-3), TimeSpan.FromMilliseconds(1));
        row.SupersededById.Should().Be(successor);
    }

    private static StateSpanEntity Legacy(Guid id, string legacyId, DateTime start, DateTime? end, Guid? supersededBy) => new()
    {
        Id = id, LegacyId = legacyId, Source = "connector", Category = "TemporaryTarget", State = "Custom",
        StartTimestamp = start, EndTimestamp = end, SupersededById = supersededBy,
    };

    private static StateSpan Sent(string originalId, DateTime start, DateTime? end) => new()
    {
        OriginalId = originalId, Source = "connector", Category = StateSpanCategory.TemporaryTarget, State = "Custom",
        StartTimestamp = start, EndTimestamp = end,
    };

    private static string Metadata(string collection, string nameKey, string name) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [StateSpanMetadataExtensions.CollectionKey] = collection,
            [nameKey] = name,
        });

    private async Task SeedAsync(params StateSpanEntity[] spans)
    {
        await using var seed = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        seed.StateSpans.AddRange(spans);
        await seed.SaveChangesAsync();
    }

    private async Task DeleteAsClampedMemberAsync(Guid id)
    {
        await using var scope = _provider.CreateAsyncScope();
        (await ClampedRepository(scope).DeleteStateSpanAsync(id.ToString())).Should().NotBeEmpty();
    }

    private StateSpanRepository ClampedRepository(AsyncServiceScope scope)
    {
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        return new StateSpanRepository(
            scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(),
            new Mock<IDuplicateLinker>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<StateSpanRepository>.Instance);
    }

    private async Task<StateSpanEntity> ReadAsync(Guid id)
    {
        await using var read = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        return await read.StateSpans.AsNoTracking().SingleAsync(s => s.Id == id);
    }
}
