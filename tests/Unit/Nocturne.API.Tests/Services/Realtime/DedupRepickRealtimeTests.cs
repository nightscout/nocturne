using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Realtime;

/// <summary>
/// A reconcile batch that re-picks a site change announces its type through the subscribers the API
/// wires: the legacy read cache clears, and no client is sent the re-pick as a new or changed record.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DedupRepickRealtimeTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime At = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly RecordingLegacyReads _legacyReads = new();
    private readonly IRecordChangePublisher _changes;

    public DedupRepickRealtimeTests() =>
        _changes = new RecordChangePublisher(
            [new LegacyReadCacheSubscriber(_legacyReads), new SignalRRecordChangeSubscriber(_broadcast.Object)]);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_re_pick_clears_the_legacy_cache_and_sends_clients_nothing()
    {
        var later = await SeedSplitSiteChangeAsync();

        var result = await DeduplicationModules.Reconciler(_db.CreateContext(TenantId).AsFactory(), changes: _changes)
            .ReconcileNewLinksAsync(5000, 10, skipBatchAfterFailures: null, CancellationToken.None);

        result.GroupsMerged.Should().Be(1);
        await using (var db = _db.CreateContext(TenantId))
            (await db.DeviceEvents.AsNoTracking().SingleAsync(e => e.Id == later)).IsDuplicate.Should().BeTrue();
        _legacyReads.Models.Should().Equal(typeof(DeviceEvent));
        _broadcast.Invocations.Should().BeEmpty("a re-pick is announced by type, and a client is sent records only");
    }

    /// <summary>The same subscribers do send a live write's records, so the test above can see a send.</summary>
    [Fact]
    public async Task A_live_write_of_a_site_change_is_sent_to_clients()
    {
        var record = new DeviceEvent { Id = Guid.CreateVersion7(), EventType = DeviceEventType.SiteChange, Timestamp = At };

        await _changes.PublishAsync(
            new RecordsChanged([new RecordTypeChange(typeof(DeviceEvent), WriteOrigin.Live, [record], [], [])]),
            CancellationToken.None);

        _broadcast.Verify(b => b.BroadcastStorageCreateAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Once);
    }

    /// <summary>
    /// One site change from two connectors 20 seconds apart, each its own group as insert-time linking
    /// left them, linked long enough ago for a reconcile to read. Returns the later copy, which reads show.
    /// </summary>
    private async Task<Guid> SeedSplitSiteChangeAsync()
    {
        await using var db = _db.CreateContext(TenantId);
        var earlier = SiteChange(At, "mylife-connector");
        var later = SiteChange(At.AddSeconds(20), "glooko-connector");
        db.DeviceEvents.AddRange(earlier, later);
        var links = new[] { PrimaryLink(earlier), PrimaryLink(later) };
        db.LinkedRecords.AddRange(links);
        await db.SaveChangesAsync();
        foreach (var link in links)
            link.SysCreatedAt = At;
        await db.SaveChangesAsync();
        return later.Id;
    }

    private static DeviceEventEntity SiteChange(DateTime at, string source) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = TenantId,
        EventType = nameof(DeviceEventType.SiteChange),
        Timestamp = at,
        DataSource = source,
    };

    private static LinkedRecordEntity PrimaryLink(DeviceEventEntity record) => new()
    {
        Id = Guid.CreateVersion7(),
        CanonicalId = Guid.CreateVersion7(),
        RecordType = RecordTypeKeys.Key(RecordType.DeviceEvent),
        RecordId = record.Id,
        SourceTimestamp = record.Timestamp.ToMills(),
        DataSource = record.DataSource!,
        IsPrimary = true,
    };
}
