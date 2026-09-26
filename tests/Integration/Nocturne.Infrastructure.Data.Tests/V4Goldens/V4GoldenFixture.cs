using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Golden-test fixture for the V4 repository dedup behaviour. Takes a migrated database from
/// <see cref="SharedPostgres"/> (production role bootstrap, migrations and startup reconcilers) and stands up the production
/// data-layer DI container via <see cref="ServiceCollectionExtensions.AddPostgreSqlInfrastructure"/>
/// pointed at that database — so goldens exercise the real repositories AND the real
/// <c>DeduplicationService</c> against real Postgres, not mocks. These goldens capture current
/// behaviour and are held identical across the V4RepositoryBase refactor.
/// </summary>
public class V4GoldenFixture : IAsyncLifetime
{
    private ServiceProvider? _provider;
    private readonly TestTenantAccessor _accessor = new();

    public string MigratorConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateMigratedDatabaseAsync("v4_goldens");
        MigratorConnectionString = database.MigratorConnectionString;
        var appConnectionString = database.AppConnectionString;

        var services = new ServiceCollection();
        services.AddLogging();
        // MutationAuditInterceptor resolves IHttpContextAccessor (registered by the API in prod).
        services.AddHttpContextAccessor();
        services.AddSingleton<ITenantAccessor>(_accessor);
        // Scoped, mirroring the API's request-scoped registration, so a golden can attribute its
        // own scope to a user without leaking that attribution into the next test.
        services.AddScoped<TestAuditContext>();
        services.AddScoped<IAuditContext>(sp => sp.GetRequiredService<TestAuditContext>());
        services.AddPostgreSqlInfrastructure(appConnectionString, configuration: null);
        // Capturing broadcaster: the V4 repos resolve IV4RecordBroadcaster<T> via the open generic, so
        // the real chokepoint fires into BroadcastCapture (additive — existing goldens ignore it).
        services.AddSingleton<BroadcastCapture>();
        services.AddScoped(typeof(IV4RecordBroadcaster<>), typeof(CapturingV4RecordBroadcaster<>));
        // Capturing legacy-Entry sink: the glucose repos resolve the optional IDataEventSink<Entry>, so the
        // chokepoint's entries projection fires into EntryCapture (additive — non-glucose goldens ignore it).
        services.AddSingleton<EntryCapture>();
        services.AddScoped<IDataEventSink<Entry>>(sp => new CapturingEntrySink(sp.GetRequiredService<EntryCapture>()));
        RegisterV4Repositories(services);
        _provider = services.BuildServiceProvider();
    }

    /// <summary>The shared broadcast collector recording every chokepoint fan-out across all V4 repos.</summary>
    public BroadcastCapture Capture => _provider!.GetRequiredService<BroadcastCapture>();

    /// <summary>The shared legacy-Entry projection collector recording the glucose-family chokepoint's entries fan-out.</summary>
    public EntryCapture EntryCapture => _provider!.GetRequiredService<EntryCapture>();

    /// <summary>
    /// Registers the V4 record repositories under test. In production these are registered by the API
    /// layer (<c>ServiceRegistrationExtensions</c>), which the data-layer test project does not
    /// reference; the impls live in Infrastructure.Data, so we wire them directly here.
    /// </summary>
    private static void RegisterV4Repositories(IServiceCollection services)
    {
        services.AddScoped<ISensorGlucoseRepository, SensorGlucoseRepository>();
        services.AddScoped<IMeterGlucoseRepository, MeterGlucoseRepository>();
        services.AddScoped<ICalibrationRepository, CalibrationRepository>();
        services.AddScoped<IBolusRepository, BolusRepository>();
        services.AddScoped<IBasalInjectionRepository, BasalInjectionRepository>();
        services.AddScoped<ITempBasalRepository, TempBasalRepository>();
        services.AddScoped<ICarbIntakeRepository, CarbIntakeRepository>();
        services.AddScoped<IBGCheckRepository, BGCheckRepository>();
        services.AddScoped<INoteRepository, NoteRepository>();
        services.AddScoped<IDeviceEventRepository, DeviceEventRepository>();
        services.AddScoped<IPatientDeviceRepository, PatientDeviceRepository>();
        services.AddScoped<IBolusCalculationRepository, BolusCalculationRepository>();
        services.AddScoped<IApsSnapshotRepository, ApsSnapshotRepository>();
        services.AddScoped<IPumpSnapshotRepository, PumpSnapshotRepository>();
        services.AddScoped<IUploaderSnapshotRepository, UploaderSnapshotRepository>();
        services.AddScoped<ITherapySettingsRepository, TherapySettingsRepository>();
        services.AddScoped<IBasalScheduleRepository, BasalScheduleRepository>();
        services.AddScoped<ICarbRatioScheduleRepository, CarbRatioScheduleRepository>();
        services.AddScoped<ISensitivityScheduleRepository, SensitivityScheduleRepository>();
        services.AddScoped<ITargetRangeScheduleRepository, TargetRangeScheduleRepository>();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    /// <summary>
    /// Seeds a fresh tenant (migrator role — the tenants table is not RLS-scoped), pins it as the
    /// current tenant, and returns a DI scope for resolving repositories under that tenant.
    /// </summary>
    public async Task<IServiceScope> BeginTenantScopeAsync(Guid tenantId)
    {
        await SeedTenantAsync(tenantId);
        PinTenant(tenantId);
        return _provider!.CreateScope();
    }

    /// <summary>
    /// Runs a read against a tenant-scoped context (via the production <c>ITenantDbContextFactory</c>)
    /// for snapshotting persisted state after a scenario.
    /// </summary>
    public async Task<T> QueryAsync<T>(Guid tenantId, Func<NocturneDbContext, Task<T>> query)
    {
        PinTenant(tenantId);
        var factory = _provider!.GetRequiredService<ITenantDbContextFactory>();
        await using var ctx = await factory.CreateAsync();
        return await query(ctx);
    }

    private void PinTenant(Guid tenantId) =>
        _accessor.SetTenant(new TenantContext(tenantId, $"t-{tenantId:N}", "Golden", IsActive: true, IsDemo: false));

    private async Task SeedTenantAsync(Guid tenantId)
    {
        // Seed via a bare EF context on the migrator connection (no interceptors, no tenant carrier):
        // TenantEntity is ISystemTimestamped, not ITenantScoped, so it is exempt from RLS.
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(MigratorConnectionString)
            .Options;
        await using var ctx = new NocturneDbContext(options);
        ctx.Tenants.Add(new TenantEntity { Id = tenantId, Slug = $"t-{tenantId:N}", DisplayName = "Golden" });
        await ctx.SaveChangesAsync();
    }
}

[CollectionDefinition("V4 goldens")]
public class V4GoldenCollection : ICollectionFixture<V4GoldenFixture>
{
}
