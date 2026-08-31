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
/// Every health record is addressable by two ids: the primary key it was assigned, and the
/// MongoDB ObjectId it carried in from the pre-migration database. The routes take the id as a
/// bare string for exactly that reason, and the resolution now lives once on
/// <c>SimpleEntityService</c> — so read, update and delete are each proven to reach a row through
/// either one.
/// </summary>
[Trait("Category", "Unit")]
public class HealthRecordIdResolutionTests
{
    private const string MongoId = "507f1f77bcf86cd799439011";
    private static readonly DateTime Taken = new(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);

    private static NocturneDbContext Context() =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options)
        {
            TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        };

    private static IDocumentProcessingService PassThrough<T>()
        where T : class, IProcessableDocument
    {
        var processing = new Mock<IDocumentProcessingService>();
        processing
            .Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<T>>()))
            .Returns((IEnumerable<T> docs) => docs);
        return processing.Object;
    }

    private static (HeartRateService service, NocturneDbContext context) HeartRates()
    {
        var context = Context();
        return (
            new HeartRateService(
                context,
                PassThrough<HeartRate>(),
                Mock.Of<ISignalRBroadcastService>(),
                NullLogger<HeartRateService>.Instance),
            context);
    }

    private static (StepCountService service, NocturneDbContext context) StepCounts()
    {
        var context = Context();
        return (
            new StepCountService(
                context,
                PassThrough<StepCount>(),
                Mock.Of<ISignalRBroadcastService>(),
                NullLogger<StepCountService>.Instance),
            context);
    }

    private static (BodyWeightService service, NocturneDbContext context) BodyWeights()
    {
        var context = Context();
        return (
            new BodyWeightService(
                context,
                PassThrough<BodyWeight>(),
                Mock.Of<ISignalRBroadcastService>(),
                NullLogger<BodyWeightService>.Instance),
            context);
    }

    [Fact]
    public async Task Heart_rate_resolves_by_the_mongo_object_id_it_was_migrated_with()
    {
        var (service, _) = HeartRates();
        await service.CreateHeartRatesAsync([new HeartRate { Id = MongoId, Timestamp = Taken, Bpm = 61 }]);

        var found = await service.GetHeartRateByIdAsync(MongoId);

        found.Should().NotBeNull("a legacy id must keep resolving after the migration");
        found!.Bpm.Should().Be(61);
    }

    [Fact]
    public async Task Heart_rate_resolves_by_its_primary_key()
    {
        var (service, context) = HeartRates();
        await service.CreateHeartRatesAsync([new HeartRate { Id = MongoId, Timestamp = Taken, Bpm = 61 }]);
        var key = (await context.HeartRates.AsNoTracking().SingleAsync()).Id;

        var found = await service.GetHeartRateByIdAsync(key.ToString());

        found.Should().NotBeNull();
        found!.Bpm.Should().Be(61);
    }

    [Fact]
    public async Task Step_count_resolves_by_the_mongo_object_id_it_was_migrated_with()
    {
        var (service, _) = StepCounts();
        await service.CreateStepCountsAsync([new StepCount { Id = MongoId, Timestamp = Taken, Metric = 1200 }]);

        (await service.GetStepCountByIdAsync(MongoId))!.Metric.Should().Be(1200);
    }

    [Fact]
    public async Task Body_weight_resolves_by_the_mongo_object_id_it_was_migrated_with()
    {
        var (service, _) = BodyWeights();
        await service.CreateBodyWeightsAsync([new BodyWeight { Id = MongoId, Mills = 1_781_000_000_000, WeightKg = 80.5m }]);

        (await service.GetBodyWeightByIdAsync(MongoId))!.WeightKg.Should().Be(80.5m);
    }

    [Fact]
    public async Task Update_by_the_mongo_object_id_writes_the_migrated_row()
    {
        var (service, context) = HeartRates();
        await service.CreateHeartRatesAsync([new HeartRate { Id = MongoId, Timestamp = Taken, Bpm = 61 }]);

        var updated = await service.UpdateHeartRateAsync(MongoId, new HeartRate { Timestamp = Taken, Bpm = 74 });

        updated.Should().NotBeNull();
        var rows = await context.HeartRates.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle("an update must find the row, not insert a second one");
        rows[0].Bpm.Should().Be(74);
    }

    [Fact]
    public async Task Delete_by_the_mongo_object_id_removes_the_migrated_row()
    {
        var (service, _) = HeartRates();
        await service.CreateHeartRatesAsync([new HeartRate { Id = MongoId, Timestamp = Taken, Bpm = 61 }]);

        (await service.DeleteHeartRateAsync(MongoId)).Should().BeTrue();
        (await service.GetHeartRateByIdAsync(MongoId)).Should().BeNull();
    }

    [Fact]
    public async Task An_id_matching_no_row_resolves_to_null_whichever_shape_it_takes()
    {
        var (service, _) = HeartRates();
        await service.CreateHeartRatesAsync([new HeartRate { Id = MongoId, Timestamp = Taken, Bpm = 61 }]);

        (await service.GetHeartRateByIdAsync("507f1f77bcf86cd799439099")).Should().BeNull();
        (await service.GetHeartRateByIdAsync(Guid.NewGuid().ToString())).Should().BeNull();
        (await service.DeleteHeartRateAsync("not-an-id")).Should().BeFalse();
    }

    [Fact]
    public async Task A_date_range_read_is_half_open_and_ordered_oldest_first()
    {
        var (service, _) = HeartRates();
        await service.CreateHeartRatesAsync([
            new HeartRate { Timestamp = Taken.AddMinutes(-5), Bpm = 55 },
            new HeartRate { Timestamp = Taken, Bpm = 61 },
            new HeartRate { Timestamp = Taken.AddMinutes(5), Bpm = 74 },
        ]);

        var range = (await service.GetHeartRatesByDateRangeAsync(Taken.AddMinutes(-5), Taken.AddMinutes(5))).ToList();

        range.Select(r => r.Bpm).Should().Equal(55, 61);
    }

    [Fact]
    public async Task A_page_read_is_newest_first()
    {
        var (service, _) = HeartRates();
        await service.CreateHeartRatesAsync([
            new HeartRate { Timestamp = Taken, Bpm = 61 },
            new HeartRate { Timestamp = Taken.AddMinutes(10), Bpm = 88 },
            new HeartRate { Timestamp = Taken.AddMinutes(-10), Bpm = 55 },
        ]);

        var page = (await service.GetHeartRatesAsync(count: 3)).ToList();

        page.Select(r => r.Bpm).Should().Equal([88, 61, 55],
            "a default page is the most recent readings, not the oldest");
    }

    [Fact]
    public async Task A_date_range_read_skips_the_oldest_records_it_is_told_to()
    {
        var (service, _) = HeartRates();
        await service.CreateHeartRatesAsync([
            new HeartRate { Timestamp = Taken.AddMinutes(-10), Bpm = 55 },
            new HeartRate { Timestamp = Taken, Bpm = 61 },
            new HeartRate { Timestamp = Taken.AddMinutes(10), Bpm = 88 },
        ]);

        var range = (await service.GetHeartRatesByDateRangeAsync(
            Taken.AddMinutes(-10), Taken.AddMinutes(11), skip: 2)).ToList();

        range.Select(r => r.Bpm).Should().Equal([88], "skip drops the oldest, the range being ascending");
    }

    [Fact]
    public async Task The_watermark_is_the_latest_timestamp_that_source_wrote()
    {
        var (service, _) = HeartRates();
        await service.CreateHeartRatesAsync([
            new HeartRate { Timestamp = Taken, Bpm = 61, DataSource = "prelude" },
            new HeartRate { Timestamp = Taken.AddMinutes(5), Bpm = 74, DataSource = "prelude" },
            new HeartRate { Timestamp = Taken.AddHours(1), Bpm = 88, DataSource = "other" },
        ]);

        (await service.GetLatestTimestampAsync("prelude")).Should().Be(Taken.AddMinutes(5));
        (await service.GetLatestTimestampAsync("never-synced")).Should().BeNull();
    }
}
