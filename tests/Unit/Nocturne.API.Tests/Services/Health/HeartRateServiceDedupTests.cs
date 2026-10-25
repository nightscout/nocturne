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
/// HeartRate counterpart of <see cref="StepCountServiceDedupTests"/> — the sync-identifier
/// upsert lives in the shared base, but this locks in that HeartRate's own service wiring
/// participates in it.
/// </summary>
public class HeartRateServiceDedupTests
{
    private static (HeartRateService service, NocturneDbContext context) CreateService()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new NocturneDbContext(options) { TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222") };

        var processing = new Mock<IDocumentProcessingService>();
        processing
            .Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<HeartRate>>()))
            .Returns((IEnumerable<HeartRate> docs) => docs);

        var service = new HeartRateService(
            context.AsFactory(),
            processing.Object,
            Mock.Of<ISignalRBroadcastService>(),
            NullLogger<HeartRateService>.Instance);

        return (service, context);
    }

    private static HeartRate Bucket(string syncId, int bpm) => new()
    {
        Timestamp = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc),
        Bpm = bpm,
        DataSource = "prelude",
        SyncIdentifier = syncId,
    };

    [Fact]
    public async Task Repeated_upload_of_same_sync_id_updates_in_place()
    {
        var (service, context) = CreateService();

        await service.CreateHeartRatesAsync([Bucket("prelude-hr-5m-1000", 60)]);
        await service.CreateHeartRatesAsync([Bucket("prelude-hr-5m-1000", 72)]);

        var rows = await context.HeartRates.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Bpm.Should().Be(72);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task Resync_of_a_deleted_record_is_blocked_only_when_the_user_deleted_it(
        bool deletedByUser, int expectedRows)
    {
        var (service, context) = CreateService();
        await service.CreateHeartRatesAsync([Bucket("prelude-hr-5m-1000", 60)]);
        var row = await context.HeartRates.SingleAsync();
        row.DeletedAt = DateTime.UtcNow;
        context.Entry(row).Property<bool>("DeletedByUser").CurrentValue = deletedByUser;
        await context.SaveChangesAsync();

        await service.CreateHeartRatesAsync([Bucket("prelude-hr-5m-1000", 72)]);

        var rows = await context.HeartRates.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().HaveCount(expectedRows);
        rows.Count(r => r.DeletedAt == null).Should().Be(expectedRows - 1);
    }

    private const string GlookoId = "6f1c0a52-3b8e-5d4a-9c2e-1f0b7a6d4e21";

    private static HeartRate GlookoShaped(int bpm) => new HeartRate
        {
            Id = GlookoId,
            Timestamp = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc),
            Bpm = bpm,
            DataSource = "glooko-connector",
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resync_of_a_deleted_record_keyed_by_id_is_skipped_or_revived_without_a_second_row(bool deletedByUser)
    {
        var (service, context) = CreateService();
        await service.CreateHeartRatesAsync([GlookoShaped(60)]);
        var row = await context.HeartRates.SingleAsync();
        row.DeletedAt = DateTime.UtcNow;
        context.Entry(row).Property<bool>("DeletedByUser").CurrentValue = deletedByUser;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var written = await service.CreateHeartRatesAsync([GlookoShaped(72)]);

        var stored = await context.HeartRates.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        stored.Id.Should().Be(Guid.Parse(GlookoId));
        if (deletedByUser)
        {
            written.Should().BeEmpty();
            stored.DeletedAt.Should().NotBeNull();
            stored.Bpm.Should().Be(60);
        }
        else
        {
            written.Should().ContainSingle();
            stored.DeletedAt.Should().BeNull();
            stored.Bpm.Should().Be(72);
        }
    }

    [Fact]
    public async Task A_live_row_governs_a_sync_key_a_user_tombstone_also_holds()
    {
        var (service, context) = CreateService();
        var tombstone = new HeartRateEntity
        {
            Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc), Bpm = 50, DataSource = "prelude", SyncIdentifier = "prelude-hr-5m-1000",
            DeletedAt = DateTime.UtcNow,
        };
        context.HeartRates.Add(tombstone);
        context.Entry(tombstone).Property<bool>("DeletedByUser").CurrentValue = true;
        var live = new HeartRateEntity
        {
            Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc), Bpm = 50, DataSource = "prelude", SyncIdentifier = "prelude-hr-5m-1000",
        };
        context.HeartRates.Add(live);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await service.CreateHeartRatesAsync([Bucket("prelude-hr-5m-1000", 72)]);

        var rows = await context.HeartRates.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.Id == live.Id).Bpm.Should().Be(72);
        rows.Single(r => r.Id == tombstone.Id).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Distinct_sync_ids_insert_separate_rows()
    {
        var (service, context) = CreateService();

        await service.CreateHeartRatesAsync([Bucket("a", 60), Bucket("b", 80)]);

        (await context.HeartRates.AsNoTracking().ToListAsync()).Should().HaveCount(2);
    }
}
