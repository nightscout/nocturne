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

namespace Nocturne.API.Integration.Tests.Health;

/// <summary>
/// A record whose primary key the writer derives from its own id, with no sync identifier, as Glooko
/// sends its health records. The soft-deleted row keeps holding that key on Postgres.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HealthRecordTombstoneTests(HealthRecordTombstoneFixture fixture)
    : IClassFixture<HealthRecordTombstoneFixture>, IAsyncLifetime
{
    private const string Source = "glooko-connector";
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly DateTime timestamp = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await using var db = Context();
        db.Tenants.Add(new TenantEntity
        {
            Id = tenantId, Slug = $"tombstone-{tenantId:N}", DisplayName = "Synthetic", IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resync_of_a_deleted_record_keyed_by_id_neither_fails_nor_adds_a_row(bool deletedByUser)
    {
        await using var db = Context();
        db.TenantId = tenantId;
        var heartRates = new HeartRateService(db.AsFactory(), Processing<HeartRate>(),
            Mock.Of<ISignalRBroadcastService>(), NullLogger<HeartRateService>.Instance);
        var stepCounts = new StepCountService(db.AsFactory(), Processing<StepCount>(),
            Mock.Of<ISignalRBroadcastService>(), NullLogger<StepCountService>.Instance);
        var bodyWeights = new BodyWeightService(db.AsFactory(), Processing<BodyWeight>(),
            Mock.Of<ISignalRBroadcastService>(), NullLogger<BodyWeightService>.Instance);
        var heartRateId = Guid.NewGuid().ToString();
        var stepCountId = Guid.NewGuid().ToString();
        var bodyWeightId = Guid.NewGuid().ToString();
        HeartRate HeartRate(int bpm) => new() { Id = heartRateId, Timestamp = timestamp, Bpm = bpm, DataSource = Source };
        StepCount StepCount(int metric) => new() { Id = stepCountId, Timestamp = timestamp, Metric = metric, DataSource = Source };
        BodyWeight BodyWeight(decimal kg) => new() { Id = bodyWeightId, Mills = 1_756_713_600_000, WeightKg = kg, DataSource = Source };

        await heartRates.CreateHeartRatesAsync([HeartRate(60)]);
        await stepCounts.CreateStepCountsAsync([StepCount(100)]);
        await bodyWeights.CreateBodyWeightsAsync([BodyWeight(70m)]);
        var deletedAt = DateTime.UtcNow;
        await Tombstone(db.HeartRates, deletedAt, deletedByUser);
        await Tombstone(db.StepCounts, deletedAt, deletedByUser);
        await Tombstone(db.BodyWeights, deletedAt, deletedByUser);

        await heartRates.CreateHeartRatesAsync([HeartRate(72)]);
        await stepCounts.CreateStepCountsAsync([StepCount(175)]);
        await bodyWeights.CreateBodyWeightsAsync([BodyWeight(71m)]);

        var heartRate = await Single(db.HeartRates);
        var stepCount = await Single(db.StepCounts);
        var bodyWeight = await Single(db.BodyWeights);
        if (deletedByUser)
        {
            Assert.NotNull(heartRate.DeletedAt);
            Assert.NotNull(stepCount.DeletedAt);
            Assert.NotNull(bodyWeight.DeletedAt);
            Assert.Equal(60, heartRate.Bpm);
            Assert.Equal(100, stepCount.Metric);
            Assert.Equal(70m, bodyWeight.WeightKg);
        }
        else
        {
            Assert.Null(heartRate.DeletedAt);
            Assert.Null(stepCount.DeletedAt);
            Assert.Null(bodyWeight.DeletedAt);
            Assert.Equal(72, heartRate.Bpm);
            Assert.Equal(175, stepCount.Metric);
            Assert.Equal(71m, bodyWeight.WeightKg);
        }
    }

    private Task<int> Tombstone<TEntity>(DbSet<TEntity> set, DateTime deletedAt, bool deletedByUser)
        where TEntity : class, ITenantScoped, ISoftDeletable =>
        set.Where(row => row.TenantId == tenantId).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.DeletedAt, deletedAt)
            .SetProperty(row => EF.Property<bool>(row, "DeletedByUser"), deletedByUser));

    private Task<TEntity> Single<TEntity>(DbSet<TEntity> set) where TEntity : class, ITenantScoped =>
        set.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.TenantId == tenantId);

    private static IDocumentProcessingService Processing<T>() where T : class, IProcessableDocument
    {
        var processing = new Mock<IDocumentProcessingService>();
        processing.Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<T>>())).Returns((IEnumerable<T> docs) => docs);
        return processing.Object;
    }

    private NocturneDbContext Context() => new(new DbContextOptionsBuilder<NocturneDbContext>()
        .UseNpgsql(fixture.Database.SuperuserConnectionString).Options);
}

public sealed class HealthRecordTombstoneFixture : IAsyncLifetime
{
    public TestDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync() =>
        Database = await SharedPostgres.CreateMigratedDatabaseAsync("health_tombstones");

    public Task DisposeAsync() => Task.CompletedTask;
}
