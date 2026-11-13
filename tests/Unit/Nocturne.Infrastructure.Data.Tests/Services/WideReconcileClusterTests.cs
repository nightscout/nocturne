using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services.Deduplication;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Services;

[CollectionDefinition(nameof(WideReconcileClusterTests), DisableParallelization = true)]
public sealed class WideReconcileClusterCollection;

/// <summary>
/// The reconcile pass over a source that writes each of its records again on every sync, soft-deleting
/// the copy before: every copy is its own group, one source cannot join itself, and the copies of one
/// record and its neighbours are value-equal inside one wide window. Measures what the pass allocates,
/// so it runs alone.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Deduplication")]
[Collection(nameof(WideReconcileClusterTests))]
public sealed class WideReconcileClusterTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Start = new(2026, 10, 28, 4, 0, 0, DateTimeKind.Utc);

    private const int Markers = 24;
    private const int CopiesPerMarker = 250;
    // This shape allocates about 0.4 GB through the pass, and 3.5 GB when every partner is kept.
    private const long MaxAllocatedBytes = 1L << 30;

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;

    public WideReconcileClusterTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();
        _context.TenantId = TestTenantId;
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task ReconcileNewLinksAsync_ResyncedTempBasals_StayApartInBoundedMemory_AndAPairBesideThemMerges()
    {
        var resyncedAt = DateTime.UtcNow.AddDays(-1);
        for (var marker = 0; marker < Markers; marker++)
        {
            var at = Start.AddMinutes(5 * marker);
            for (var copy = 0; copy < CopiesPerMarker; copy++)
            {
                var deletedAt = copy < CopiesPerMarker - 1 ? resyncedAt : (DateTime?)null;
                AddTempBasal(at, DataSources.CareLinkConnector, deletedAt);
            }
        }

        var pairAt = Start.AddHours(6);
        var glooko = AddTempBasal(pairAt, "glooko-connector", null);
        var mylife = AddTempBasal(pairAt.AddSeconds(90), "mylife-connector", null);
        await _context.SaveChangesAsync();
        await AgeAllLinks();

        var service = DeduplicationModules.Reconciler(_context.AsFactory());
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var result = await service.ReconcileNewLinksAsync(
            batchSize: 10_000, maxBatches: 1, skipBatchAfterFailures: null, CancellationToken.None);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        result.GroupsMerged.Should().Be(1);
        var links = await _context.LinkedRecords.AsNoTracking().ToListAsync();
        links.Single(l => l.RecordId == glooko).CanonicalId
            .Should().Be(links.Single(l => l.RecordId == mylife).CanonicalId);
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(Markers * CopiesPerMarker + 1,
            "one source's copies of one record are never joined to each other");
        allocated.Should().BeLessThan(MaxAllocatedBytes,
            "the wide pass keeps at most one partner per group, not every value-equal group in its window");
    }

    private Guid AddTempBasal(DateTime at, string source, DateTime? deletedAt)
    {
        var id = Guid.CreateVersion7();
        _context.TempBasals.Add(new TempBasalEntity
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = at,
            EndTimestamp = at.AddMinutes(5),
            Rate = 0.6,
            Origin = "Algorithm",
            DataSource = source,
            DeletedAt = deletedAt
        });
        _context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            CanonicalId = Guid.CreateVersion7(),
            RecordType = RecordTypeKeys.Key(RecordType.TempBasal),
            RecordId = id,
            SourceTimestamp = at.ToMills(),
            DataSource = source,
            IsPrimary = true
        });
        return id;
    }

    private async Task AgeAllLinks()
    {
        var aged = DateTime.UtcNow.AddHours(-1);
        foreach (var link in await _context.LinkedRecords.ToListAsync())
            link.SysCreatedAt = aged;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
