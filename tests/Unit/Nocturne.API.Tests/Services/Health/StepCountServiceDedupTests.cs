using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Health;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Health;

/// <summary>
/// Verifies the sync-identifier upsert that <see cref="StepCountService"/> inherits from
/// <c>SimpleEntityService</c>: a create whose (DataSource, SyncIdentifier) matches an existing
/// row updates it in place, making repeated uploads of the same bucket idempotent.
/// </summary>
public class StepCountServiceDedupTests
{
    private static (StepCountService service, NocturneDbContext context) CreateService()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new NocturneDbContext(options) { TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111") };

        var processing = new Mock<IDocumentProcessingService>();
        processing
            .Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<StepCount>>()))
            .Returns((IEnumerable<StepCount> docs) => docs);

        var service = new StepCountService(
            context,
            processing.Object,
            Mock.Of<ISignalRBroadcastService>(),
            NullLogger<StepCountService>.Instance);

        return (service, context);
    }

    private static StepCount Bucket(string syncId, int metric, string dataSource = "prelude") => new()
    {
        Timestamp = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc),
        Metric = metric,
        Source = 0,
        DataSource = dataSource,
        SyncIdentifier = syncId,
    };

    [Fact]
    public async Task Repeated_upload_of_same_sync_id_updates_in_place()
    {
        var (service, context) = CreateService();

        await service.CreateStepCountsAsync([Bucket("prelude-steps-5m-1000", 100)]);
        await service.CreateStepCountsAsync([Bucket("prelude-steps-5m-1000", 175)]);

        var rows = await context.StepCounts.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle("the same (DataSource, SyncIdentifier) must upsert, not duplicate");
        rows[0].Metric.Should().Be(175, "the re-upload carries the corrected value");
    }

    [Fact]
    public async Task Distinct_sync_ids_insert_separate_rows()
    {
        var (service, context) = CreateService();

        await service.CreateStepCountsAsync([Bucket("a", 100), Bucket("b", 200)]);

        var rows = await context.StepCounts.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Duplicate_sync_id_within_one_batch_collapses_to_last()
    {
        var (service, context) = CreateService();

        await service.CreateStepCountsAsync([Bucket("dup", 100), Bucket("dup", 250)]);

        var rows = await context.StepCounts.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle("an intra-batch duplicate must not collide on the unique key");
        rows[0].Metric.Should().Be(250);
    }

    [Fact]
    public async Task Records_without_sync_identifier_always_insert()
    {
        var (service, context) = CreateService();

        await service.CreateStepCountsAsync([Bucket(syncId: null!, metric: 100, dataSource: null!)]);
        await service.CreateStepCountsAsync([Bucket(syncId: null!, metric: 100, dataSource: null!)]);

        var rows = await context.StepCounts.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2, "without a sync key, the blind-insert behaviour is preserved");
    }
}
