using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Events;
using Nocturne.API.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Caching;

/// <summary>
/// The legacy read families are cleared from the repositories, whatever wrote through them, so the
/// contract is pinned over the repositories and the map, not over any caller.
/// </summary>
[Trait("Category", "Unit")]
public class LegacyReadCacheInvalidationTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly RecordingLegacyReads _written = new();

    public LegacyReadCacheInvalidationTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext($"legacy_read_cache_{Guid.NewGuid()}");
        _context.TenantId = Tenant;
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly Type[] GlucoseModels = [typeof(SensorGlucose), typeof(MeterGlucose), typeof(Calibration)];

    private static IEnumerable<Type> TreatmentModels =>
        LegacyTreatmentTables.All.Select(t => t.Model)
            .Append(typeof(TreatmentFood)).Append(typeof(Nocturne.Core.Models.Food)).Distinct();

    public static TheoryData<Type> RepositoryModels()
    {
        var data = new TheoryData<Type>();
        foreach (var model in TreatmentModels.Concat(GlucoseModels)
                     .Where(m => m != typeof(StateSpan) && m != typeof(TreatmentFood) && m != typeof(Nocturne.Core.Models.Food)))
            data.Add(model);
        return data;
    }

    [Fact]
    public void Every_registry_table_and_the_food_breakdown_map_to_treatments_only()
    {
        foreach (var model in TreatmentModels)
            LegacyReadCacheInvalidation.FamiliesByModel[model].Should().Equal([TenantCacheFamily.Treatments], model.Name);
    }

    [Fact]
    public void Every_glucose_table_maps_to_entries_only()
    {
        foreach (var model in GlucoseModels)
            LegacyReadCacheInvalidation.FamiliesByModel[model].Should().Equal([TenantCacheFamily.Entries], model.Name);
    }

    [Fact]
    public void Nothing_else_maps_to_a_family()
    {
        LegacyReadCacheInvalidation.FamiliesByModel.Keys
            .Should().BeEquivalentTo(TreatmentModels.Concat(GlucoseModels));
    }

    /// <summary>
    /// Every repository takes the port as optional, so without the API's registration every write would
    /// clear nothing and every other test here would still pass.
    /// </summary>
    [Fact]
    public async Task The_api_registrations_give_a_repository_the_port_that_clears_the_cache()
    {
        var services = new ServiceCollection().AddDomainServices().AddV4Infrastructure();
        services.Should().ContainSingle(d => d.ServiceType == typeof(ILegacyReadCacheInvalidation))
            .Which.ImplementationType.Should().Be<LegacyReadCacheInvalidation>();

        var cache = new Mock<ITenantCache>();
        services.RemoveAll<IV4RecordBroadcaster<Bolus>>();
        services
            .AddLogging()
            .AddSingleton<INocturneDbContextFactory>(new TestNocturneDbContextFactory(_context))
            .AddSingleton(Mock.Of<IDeduplicationService>())
            .AddSingleton<IAuditContext>(new SystemAuditContext())
            .AddSingleton(cache.Object);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IBolusRepository>().CreateAsync(
            new Bolus { Timestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc), Insulin = 1 }, WriteOrigin.Live);

        cache.Verify(c => c.RemoveFamilyAsync(TenantCacheFamily.Treatments, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task A_write_clears_each_family_once_and_a_type_no_read_serves_clears_nothing()
    {
        var cache = new Mock<ITenantCache>();
        var invalidation = new LegacyReadCacheInvalidation(cache.Object, NullLogger<LegacyReadCacheInvalidation>.Instance);

        await invalidation.WrittenAsync([typeof(Bolus), typeof(CarbIntake), typeof(ApsSnapshot)]);

        cache.Verify(c => c.RemoveFamilyAsync(TenantCacheFamily.Treatments, It.IsAny<CancellationToken>()), Times.Once);
        cache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_food_write_clears_the_treatments_family_a_treatment_is_served_through()
    {
        var cache = new Mock<ITenantCache>();
        var invalidation = new LegacyReadCacheInvalidation(cache.Object, NullLogger<LegacyReadCacheInvalidation>.Instance);

        await invalidation.WrittenAsync([typeof(Nocturne.Core.Models.Food)]);

        cache.Verify(c => c.RemoveFamilyAsync(TenantCacheFamily.Treatments, It.IsAny<CancellationToken>()), Times.Once);
        cache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_failed_clear_does_not_fault_the_write()
    {
        var cache = new Mock<ITenantCache>();
        cache.Setup(c => c.RemoveFamilyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache down"));
        var invalidation = new LegacyReadCacheInvalidation(cache.Object, NullLogger<LegacyReadCacheInvalidation>.Instance);

        var write = () => invalidation.WrittenAsync([typeof(SensorGlucose)]);

        await write.Should().NotThrowAsync();
    }

    [Theory]
    [MemberData(nameof(RepositoryModels))]
    public async Task A_backfill_create_through_the_repository_reports_its_model(Type model)
    {
        var repository = Repository(model);
        var record = (IV4Record)Activator.CreateInstance(model)!;
        record.Timestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

        await (Task)typeof(IV4Repository<>).MakeGenericType(model)
            .GetMethod(nameof(IV4Repository<Bolus>.CreateAsync))!
            .Invoke(repository, [record, WriteOrigin.Backfill, CancellationToken.None])!;

        _written.Models.Should().NotBeEmpty().And.OnlyContain(written => written == model);
    }

    [Fact]
    public async Task An_update_the_material_gate_does_not_count_still_reports()
    {
        var repository = (BolusRepository)Repository(typeof(Bolus));
        var bolus = await repository.CreateAsync(
            new Bolus { Timestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc), Insulin = 1 }, WriteOrigin.Live);
        _written.Models.Clear();

        bolus.CorrelationId = Guid.NewGuid();
        await repository.UpdateAsync(bolus.Id, bolus, WriteOrigin.Live);

        _written.Models.Should().Equal(typeof(Bolus));
    }

    [Fact]
    public async Task A_delete_through_the_repository_reports_its_model()
    {
        var repository = (BolusRepository)Repository(typeof(Bolus));
        var bolus = await repository.CreateAsync(
            new Bolus { Timestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc), Insulin = 1 }, WriteOrigin.Live);
        _written.Models.Clear();

        await repository.DeleteAsync(bolus.Id, WriteOrigin.Live);

        _written.Models.Should().Equal(typeof(Bolus));
    }

    [Fact]
    public async Task A_state_span_write_reports_state_spans()
    {
        var repository = ActivatorUtilities.CreateInstance<StateSpanRepository>(Services());

        await repository.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = "Custom",
            StartTimestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
        });

        _written.Models.Should().Equal(typeof(StateSpan));
    }

    [Fact]
    public async Task A_food_breakdown_write_reports_treatment_foods()
    {
        var repository = ActivatorUtilities.CreateInstance<TreatmentFoodRepository>(Services());

        await repository.CreateAsync(new TreatmentFood { CarbIntakeId = Guid.NewGuid(), Carbs = 10 });

        _written.Models.Should().Equal(typeof(TreatmentFood));
    }

    [Fact]
    public async Task Settling_an_activations_end_reports_profile_activations()
    {
        var repository = (ProfileActivationRepository)Repository(typeof(ProfileActivation));
        var activation = await repository.CreateAsync(
            new ProfileActivation
            {
                Timestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
                BaseProfileName = "Default",
            },
            WriteOrigin.Live);
        _written.Models.Clear();

        await repository.SettleAsync(
            new Dictionary<Guid, DateTime?> { [activation.Id] = new DateTime(2026, 5, 1, 13, 0, 0, DateTimeKind.Utc) },
            new Dictionary<Guid, Guid?>());

        _written.Models.Should().Equal(typeof(ProfileActivation));
    }

    private IServiceProvider Services() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<INocturneDbContextFactory>(new TestNocturneDbContextFactory(_context))
            .AddSingleton(Mock.Of<IDeduplicationService>())
            .AddSingleton<IAuditContext>(new SystemAuditContext())
            .AddSingleton<ILegacyReadCacheInvalidation>(_written)
            .BuildServiceProvider();

    /// <summary>The concrete repository of <paramref name="model"/>, built as DI builds it.</summary>
    private object Repository(Type model)
    {
        var repositoryType = typeof(BolusRepository).Assembly.GetTypes().Single(t =>
            t is { IsClass: true, IsAbstract: false } && IsRepositoryOf(t, model));
        return ActivatorUtilities.CreateInstance(Services(), repositoryType);
    }

    private static bool IsRepositoryOf(Type type, Type model)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(V4RepositoryBase<,>))
                return t.GetGenericArguments()[0] == model;
        }

        return false;
    }
}
