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
/// BodyWeight dedup — distinct from StepCount/HeartRate because BodyWeight is mills-based
/// (not Timestamp) and its <c>syncIdentifier</c> flows through the model POST + the
/// <c>[NocturneOnly]</c> attribute rather than an Upsert request DTO.
/// </summary>
public class BodyWeightServiceDedupTests
{
    private static (BodyWeightService service, NocturneDbContext context) CreateService()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new NocturneDbContext(options) { TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333") };

        var processing = new Mock<IDocumentProcessingService>();
        processing
            .Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<BodyWeight>>()))
            .Returns((IEnumerable<BodyWeight> docs) => docs);

        var service = new BodyWeightService(
            context.AsFactory(),
            processing.Object,
            Mock.Of<ISignalRBroadcastService>(),
            NullLogger<BodyWeightService>.Instance);

        return (service, context);
    }

    private static BodyWeight Reading(string syncId, decimal weightKg) => new()
    {
        Mills = 1_700_000_000_000,
        WeightKg = weightKg,
        DataSource = "prelude",
        SyncIdentifier = syncId,
    };

    [Fact]
    public async Task Repeated_upload_of_same_sync_id_updates_in_place()
    {
        var (service, context) = CreateService();

        await service.CreateBodyWeightsAsync([Reading("prelude-weight-abc", 80.5m)]);
        await service.CreateBodyWeightsAsync([Reading("prelude-weight-abc", 81.2m)]);

        var rows = await context.BodyWeights.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].WeightKg.Should().Be(81.2m);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task Resync_of_a_deleted_record_is_blocked_only_when_the_user_deleted_it(
        bool deletedByUser, int expectedRows)
    {
        var (service, context) = CreateService();
        await service.CreateBodyWeightsAsync([Reading("prelude-weight-abc", 80.5m)]);
        var row = await context.BodyWeights.SingleAsync();
        row.DeletedAt = DateTime.UtcNow;
        context.Entry(row).Property<bool>("DeletedByUser").CurrentValue = deletedByUser;
        await context.SaveChangesAsync();

        await service.CreateBodyWeightsAsync([Reading("prelude-weight-abc", 81.2m)]);

        var rows = await context.BodyWeights.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().HaveCount(expectedRows);
        rows.Count(r => r.DeletedAt == null).Should().Be(expectedRows - 1);
    }

    private const string GlookoId = "6f1c0a52-3b8e-5d4a-9c2e-1f0b7a6d4e21";

    private static BodyWeight GlookoShaped(decimal weightKg) => new BodyWeight
        {
            Id = GlookoId,
            Mills = 1_700_000_000_000,
            WeightKg = weightKg,
            DataSource = "glooko-connector",
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resync_of_a_deleted_record_keyed_by_id_is_skipped_or_revived_without_a_second_row(bool deletedByUser)
    {
        var (service, context) = CreateService();
        await service.CreateBodyWeightsAsync([GlookoShaped(80.5m)]);
        var row = await context.BodyWeights.SingleAsync();
        row.DeletedAt = DateTime.UtcNow;
        context.Entry(row).Property<bool>("DeletedByUser").CurrentValue = deletedByUser;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var written = await service.CreateBodyWeightsAsync([GlookoShaped(81.2m)]);

        var stored = await context.BodyWeights.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        stored.Id.Should().Be(Guid.Parse(GlookoId));
        if (deletedByUser)
        {
            written.Should().BeEmpty();
            stored.DeletedAt.Should().NotBeNull();
            stored.WeightKg.Should().Be(80.5m);
        }
        else
        {
            written.Should().ContainSingle();
            stored.DeletedAt.Should().BeNull();
            stored.WeightKg.Should().Be(81.2m);
        }
    }

    [Fact]
    public async Task A_live_row_governs_a_sync_key_a_user_tombstone_also_holds()
    {
        var (service, context) = CreateService();
        var tombstone = new BodyWeightEntity
        {
            Id = Guid.NewGuid(), Mills = 1_700_000_000_000, WeightKg = 70m, DataSource = "prelude", SyncIdentifier = "prelude-weight-abc",
            DeletedAt = DateTime.UtcNow,
        };
        context.BodyWeights.Add(tombstone);
        context.Entry(tombstone).Property<bool>("DeletedByUser").CurrentValue = true;
        var live = new BodyWeightEntity
        {
            Id = Guid.NewGuid(), Mills = 1_700_000_000_000, WeightKg = 70m, DataSource = "prelude", SyncIdentifier = "prelude-weight-abc",
        };
        context.BodyWeights.Add(live);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await service.CreateBodyWeightsAsync([Reading("prelude-weight-abc", 81.2m)]);

        var rows = await context.BodyWeights.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.Id == live.Id).WeightKg.Should().Be(81.2m);
        rows.Single(r => r.Id == tombstone.Id).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Distinct_sync_ids_insert_separate_rows()
    {
        var (service, context) = CreateService();

        await service.CreateBodyWeightsAsync([Reading("w1", 80m), Reading("w2", 81m)]);

        (await context.BodyWeights.AsNoTracking().ToListAsync()).Should().HaveCount(2);
    }
}
