using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Testcontainers.PostgreSql;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Golden-test fixture for the V4 repository dedup behaviour. Spins up a real PostgreSQL container
/// (same role bootstrap + migrations as <c>RlsCompletenessFixture</c>) and stands up the production
/// data-layer DI container via <see cref="ServiceCollectionExtensions.AddPostgreSqlInfrastructure"/>
/// pointed at the container — so goldens exercise the real repositories AND the real
/// <c>DeduplicationService</c> against real Postgres, not mocks. These goldens capture current
/// behaviour and are held identical across the V4RepositoryBase refactor.
/// </summary>
public class V4GoldenFixture : IAsyncLifetime
{
    private const string DbName = "nocturne_v4_goldens";
    private const string BootstrapUser = "postgres";
    private const string BootstrapPassword = "bootstrap-test-password";
    private const string MigratorPassword = "v4-goldens-migrator-password";
    private const string AppPassword = "v4-goldens-app-password";
    private const string WebPassword = "v4-goldens-web-password";

    private PostgreSqlContainer? _container;
    private ServiceProvider? _provider;
    private readonly TestTenantAccessor _accessor = new();

    public string MigratorConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var initScriptPath = ResolveInitScriptPath();

        _container = new PostgreSqlBuilder()
            .WithImage("postgres:17.6")
            .WithDatabase(DbName)
            .WithUsername(BootstrapUser)
            .WithPassword(BootstrapPassword)
            .WithEnvironment("NOCTURNE_MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("NOCTURNE_APP_PASSWORD", AppPassword)
            .WithEnvironment("NOCTURNE_WEB_PASSWORD", WebPassword)
            .WithBindMount(initScriptPath, "/docker-entrypoint-initdb.d/00-init.sh")
            .Build();

        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(5432);

        MigratorConnectionString =
            $"Host={host};Port={port};Database={DbName};Username=nocturne_migrator;Password={MigratorPassword}";
        var appConnectionString =
            $"Host={host};Port={port};Database={DbName};Username=nocturne_app;Password={AppPassword}";

        await DatabaseInitializationExtensions.RunMigrationsAsync(
            MigratorConnectionString,
            NullLogger.Instance,
            new TenantConnectionInterceptor());

        await DatabaseInitializationExtensions.ReconcileShareRlsPoliciesAsync(
            MigratorConnectionString,
            NullLogger.Instance);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PostgreSql:ConnectionString"] = appConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // MutationAuditInterceptor resolves IHttpContextAccessor (registered by the API in prod).
        services.AddHttpContextAccessor();
        services.AddSingleton<ITenantAccessor>(_accessor);
        services.AddSingleton<IAuditContext, SystemAuditContext>();
        services.AddPostgreSqlInfrastructure(config);
        RegisterV4Repositories(services);
        _provider = services.BuildServiceProvider();
    }

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

        if (_container is not null)
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
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
        _accessor.SetTenant(new TenantContext(tenantId, $"t-{tenantId:N}", "Golden", IsActive: true));

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

    private static string ResolveInitScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "docs/postgres/container-init/00-init.sh")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate docs/postgres/container-init/00-init.sh by walking up from " + AppContext.BaseDirectory);
        }

        return Path.Join(dir.FullName, "docs/postgres/container-init/00-init.sh");
    }
}

[CollectionDefinition("V4 goldens")]
public class V4GoldenCollection : ICollectionFixture<V4GoldenFixture>
{
}
