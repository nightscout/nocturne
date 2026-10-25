using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Health;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
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
            context.AsFactory(),
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

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task Resync_of_a_deleted_record_is_blocked_only_when_the_user_deleted_it(
        bool deletedByUser, int expectedRows)
    {
        var (service, context) = CreateService();
        await service.CreateStepCountsAsync([Bucket("prelude-steps-5m-1000", 100)]);
        var row = await context.StepCounts.SingleAsync();
        row.DeletedAt = DateTime.UtcNow;
        context.Entry(row).Property<bool>("DeletedByUser").CurrentValue = deletedByUser;
        await context.SaveChangesAsync();

        await service.CreateStepCountsAsync([Bucket("prelude-steps-5m-1000", 175)]);

        var rows = await context.StepCounts.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().HaveCount(expectedRows);
        rows.Count(r => r.DeletedAt == null).Should().Be(expectedRows - 1);
    }

    private const string GlookoId = "6f1c0a52-3b8e-5d4a-9c2e-1f0b7a6d4e21";

    private static StepCount GlookoShaped(int metric) => new StepCount
        {
            Id = GlookoId,
            Timestamp = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc),
            Metric = metric,
            DataSource = "glooko-connector",
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resync_of_a_deleted_record_keyed_by_id_is_skipped_or_revived_without_a_second_row(bool deletedByUser)
    {
        var (service, context) = CreateService();
        await service.CreateStepCountsAsync([GlookoShaped(100)]);
        var row = await context.StepCounts.SingleAsync();
        row.DeletedAt = DateTime.UtcNow;
        context.Entry(row).Property<bool>("DeletedByUser").CurrentValue = deletedByUser;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var written = await service.CreateStepCountsAsync([GlookoShaped(175)]);

        var stored = await context.StepCounts.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        stored.Id.Should().Be(Guid.Parse(GlookoId));
        if (deletedByUser)
        {
            written.Should().BeEmpty();
            stored.DeletedAt.Should().NotBeNull();
            stored.Metric.Should().Be(100);
        }
        else
        {
            written.Should().ContainSingle();
            stored.DeletedAt.Should().BeNull();
            stored.Metric.Should().Be(175);
        }
    }

    [Fact]
    public async Task A_live_row_governs_a_sync_key_a_user_tombstone_also_holds()
    {
        var (service, context) = CreateService();
        var tombstone = new StepCountEntity
        {
            Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc), Metric = 50, DataSource = "prelude", SyncIdentifier = "prelude-steps-5m-1000",
            DeletedAt = DateTime.UtcNow,
        };
        context.StepCounts.Add(tombstone);
        context.Entry(tombstone).Property<bool>("DeletedByUser").CurrentValue = true;
        var live = new StepCountEntity
        {
            Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc), Metric = 50, DataSource = "prelude", SyncIdentifier = "prelude-steps-5m-1000",
        };
        context.StepCounts.Add(live);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await service.CreateStepCountsAsync([Bucket("prelude-steps-5m-1000", 175)]);

        var rows = await context.StepCounts.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.Id == live.Id).Metric.Should().Be(175);
        rows.Single(r => r.Id == tombstone.Id).DeletedAt.Should().NotBeNull();
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
