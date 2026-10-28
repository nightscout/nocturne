using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Health;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Integration.Tests.Health;

/// <summary>
/// Two tenants fed from one upstream send the same record id, hashed or a GUID. Runs as the app
/// role, so each lookup sees only its own tenant, as in production. See
/// <c>NocturneDbContext.ScopeKeyToTenant</c> for why the id must not be unique across tenants.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CrossTenantRecordIdTests(CrossTenantRecordIdFixture fixture)
    : IClassFixture<CrossTenantRecordIdFixture>, IAsyncLifetime
{
    private const string Source = "nightscout-connector";
    private readonly Guid tenantA = Guid.NewGuid();
    private readonly Guid tenantB = Guid.NewGuid();
    private readonly DateTime timestamp = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(fixture.Database.SuperuserConnectionString).Options);
        foreach (var tenant in new[] { tenantA, tenantB })
            db.Tenants.Add(new TenantEntity
            {
                Id = tenant, Slug = $"cross-{tenant:N}", DisplayName = "Synthetic", IsActive = true,
            });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public static TheoryData<string> SharedIds => new()
    {
        "hr-shared-upstream-id",
        "0b5f3a52-6a0b-4c4c-9a53-2f6f1c2a9e11",
    };

    [Theory]
    [MemberData(nameof(SharedIds))]
    public async Task Both_tenants_keep_a_heart_rate_with_the_same_id(string id)
    {
        await HeartRates(tenantA).CreateHeartRatesAsync([new HeartRate { Id = id, Timestamp = timestamp, Bpm = 60 }]);
        await HeartRates(tenantB).CreateHeartRatesAsync([new HeartRate { Id = id, Timestamp = timestamp, Bpm = 90 }]);

        Assert.Equal(60, (await Tenant(tenantA).HeartRates.SingleAsync()).Bpm);
        Assert.Equal(90, (await Tenant(tenantB).HeartRates.SingleAsync()).Bpm);
    }

    [Theory]
    [MemberData(nameof(SharedIds))]
    public async Task Both_tenants_keep_a_step_count_with_the_same_id(string id)
    {
        await StepCounts(tenantA).CreateStepCountsAsync([new StepCount { Id = id, Timestamp = timestamp, Metric = 100 }]);
        await StepCounts(tenantB).CreateStepCountsAsync([new StepCount { Id = id, Timestamp = timestamp, Metric = 900 }]);

        Assert.Equal(100, (await Tenant(tenantA).StepCounts.SingleAsync()).Metric);
        Assert.Equal(900, (await Tenant(tenantB).StepCounts.SingleAsync()).Metric);
    }

    [Theory]
    [MemberData(nameof(SharedIds))]
    public async Task Both_tenants_keep_a_body_weight_with_the_same_id(string id)
    {
        await BodyWeights(tenantA).CreateBodyWeightsAsync([new BodyWeight { Id = id, Mills = 1_756_713_600_000, WeightKg = 70m }]);
        await BodyWeights(tenantB).CreateBodyWeightsAsync([new BodyWeight { Id = id, Mills = 1_756_713_600_000, WeightKg = 90m }]);

        Assert.Equal(70m, (await Tenant(tenantA).BodyWeights.SingleAsync()).WeightKg);
        Assert.Equal(90m, (await Tenant(tenantB).BodyWeights.SingleAsync()).WeightKg);
    }

    [Theory]
    [MemberData(nameof(SharedIds))]
    public async Task Both_tenants_keep_a_system_event_with_the_same_id(string id)
    {
        await new SystemEventRepository(Tenant(tenantA).AsFactory(), TimeProvider.System).BulkUpsertAsync([Event(id, "a")]);
        await new SystemEventRepository(Tenant(tenantB).AsFactory(), TimeProvider.System).BulkUpsertAsync([Event(id, "b")]);

        Assert.Equal("a", (await Tenant(tenantA).SystemEvents.SingleAsync()).Code);
        Assert.Equal("b", (await Tenant(tenantB).SystemEvents.SingleAsync()).Code);
    }

    [Theory]
    [MemberData(nameof(SharedIds))]
    public async Task Both_tenants_keep_a_setting_with_the_same_id(string id)
    {
        await new SettingsRepository(Tenant(tenantA).AsFactory()).CreateSettingsAsync([new Settings { Id = id, Key = "units", Value = "mmol" }]);
        await new SettingsRepository(Tenant(tenantB).AsFactory()).CreateSettingsAsync([new Settings { Id = id, Key = "units", Value = "mgdl" }]);

        Assert.Contains("mmol", (await Tenant(tenantA).Settings.SingleAsync()).Value);
        Assert.Contains("mgdl", (await Tenant(tenantB).Settings.SingleAsync()).Value);
    }

    [Theory]
    [MemberData(nameof(SharedIds))]
    public async Task Both_tenants_keep_a_food_with_the_same_id(string id)
    {
        await Foods(tenantA).CreateFoodAsync([new Food { Id = id, Name = "toast", Carbs = 15 }]);
        await Foods(tenantB).CreateFoodAsync([new Food { Id = id, Name = "rice", Carbs = 45 }]);

        Assert.Equal("toast", (await Tenant(tenantA).Foods.SingleAsync()).Name);
        Assert.Equal("rice", (await Tenant(tenantB).Foods.SingleAsync()).Name);
    }

    [Theory]
    [MemberData(nameof(SharedIds))]
    public async Task Both_tenants_keep_a_superseded_state_span_with_the_same_ids(string id)
    {
        foreach (var (tenant, state) in new[] { (tenantA, "a"), (tenantB, "b") })
            await StateSpans(tenant).BulkUpsertAsync(
            [
                TemporaryTarget($"{id}:older", state, timestamp),
                TemporaryTarget(id, state, timestamp.AddHours(1)),
            ]);

        foreach (var (tenant, state) in new[] { (tenantA, "a"), (tenantB, "b") })
        {
            await using var db = Tenant(tenant);
            var spans = await db.StateSpans.AsNoTracking().OrderBy(s => s.StartTimestamp).ToListAsync();
            Assert.Equal([state, state], spans.Select(s => s.State));
            Assert.Equal(spans[1].Id, spans[0].SupersededById);
            Assert.Equal(timestamp.AddHours(1), spans[0].EndTimestamp);
        }
    }

    [Theory]
    [MemberData(nameof(SharedIds))]
    public async Task Both_tenants_keep_a_sleep_session_and_its_stages_with_the_same_id(string id)
    {
        await new SleepSessionRepository(Tenant(tenantA).AsFactory(), TimeProvider.System).UpsertSessionAsync(Sleep(id, SleepStageType.Deep));
        await new SleepSessionRepository(Tenant(tenantB).AsFactory(), TimeProvider.System).UpsertSessionAsync(Sleep(id, SleepStageType.Rem));

        foreach (var (tenant, stage) in new[] { (tenantA, SleepStageType.Deep), (tenantB, SleepStageType.Rem) })
        {
            await using var db = Tenant(tenant);
            var session = await db.SleepSessions.Include(s => s.Stages).AsNoTracking().SingleAsync();
            Assert.Equal(stage.ToString(), Assert.Single(session.Stages).Stage);
        }
    }

    [Theory]
    [MemberData(nameof(SharedIds))]
    public async Task A_resent_record_updates_the_row_it_was_stored_as(string id)
    {
        await HeartRates(tenantB).CreateHeartRatesAsync([new HeartRate { Id = id, Timestamp = timestamp, Bpm = 90 }]);
        await HeartRates(tenantA).CreateHeartRatesAsync([new HeartRate { Id = id, Timestamp = timestamp, Bpm = 60 }]);
        var stored = await HeartRateRow(tenantA);

        await HeartRates(tenantA).CreateHeartRatesAsync([new HeartRate { Id = id, Timestamp = timestamp, Bpm = 72 }]);

        var resent = await HeartRateRow(tenantA);
        Assert.Equal(stored.Id, resent.Id);
        Assert.Equal(72, resent.Bpm);
        Assert.Equal(90, (await HeartRateRow(tenantB)).Bpm);
    }

    private async Task<HeartRateEntity> HeartRateRow(Guid tenant)
    {
        await using var db = Tenant(tenant);
        return await db.HeartRates.AsNoTracking().SingleAsync();
    }

    private static SystemEvent Event(string id, string code) => new()
    {
        Id = id,
        EventType = SystemEventType.Info,
        Category = SystemEventCategory.Pump,
        Code = code,
        Mills = 1_756_713_600_000,
    };

    private StateSpan TemporaryTarget(string id, string state, DateTime start) => new()
    {
        Id = id,
        OriginalId = id,
        Category = StateSpanCategory.TemporaryTarget,
        State = state,
        StartTimestamp = start,
        Source = Source,
    };

    private SleepSession Sleep(string id, SleepStageType stage) => new()
    {
        Id = id,
        StartTime = timestamp,
        EndTime = timestamp.AddHours(8),
        Source = SleepSource.Oura,
        Stages = [new SleepStageInterval { StartTime = timestamp, EndTime = timestamp.AddHours(1), Stage = stage }],
    };

    private FoodRepository Foods(Guid tenant) => new(Tenant(tenant).AsFactory(), NullLogger<FoodRepository>.Instance);

    private StateSpanRepository StateSpans(Guid tenant) => new(
        Tenant(tenant).AsFactory(), Mock.Of<IDeduplicationService>(), Mock.Of<IAuditContext>(),
        NullLogger<StateSpanRepository>.Instance, TimeProvider.System);

    private BodyWeightService BodyWeights(Guid tenant) => new(
        Tenant(tenant).AsFactory(), Processing<BodyWeight>(), Mock.Of<ISignalRBroadcastService>(),
        NullLogger<BodyWeightService>.Instance);

    private HeartRateService HeartRates(Guid tenant) => new(
        Tenant(tenant).AsFactory(), Processing<HeartRate>(), Mock.Of<ISignalRBroadcastService>(),
        NullLogger<HeartRateService>.Instance);

    private StepCountService StepCounts(Guid tenant) => new(
        Tenant(tenant).AsFactory(), Processing<StepCount>(), Mock.Of<ISignalRBroadcastService>(),
        NullLogger<StepCountService>.Instance);

    private NocturneDbContext Tenant(Guid tenant) => new(new DbContextOptionsBuilder<NocturneDbContext>()
        .UseNpgsql(fixture.Database.AppConnectionString)
        .AddInterceptors(new TenantConnectionInterceptor())
        .Options)
    {
        TenantId = tenant,
    };

    private static IDocumentProcessingService Processing<T>() where T : class, IProcessableDocument
    {
        var processing = new Mock<IDocumentProcessingService>();
        processing.Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<T>>())).Returns((IEnumerable<T> docs) => docs);
        return processing.Object;
    }
}

public sealed class CrossTenantRecordIdFixture : IAsyncLifetime
{
    public TestDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync() =>
        Database = await SharedPostgres.CreateMigratedDatabaseAsync("cross_tenant_ids");

    public Task DisposeAsync() => Task.CompletedTask;
}
