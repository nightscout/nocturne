using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Storage;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Security;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Service collection extensions for PostgreSQL data infrastructure
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add PostgreSQL data services with explicit connection string
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="configuration">
    /// Application configuration whose <see cref="PostgreSqlConfiguration.SectionName"/> section is
    /// bound over the defaults, so every setting on <see cref="PostgreSqlConfiguration"/> is
    /// reachable from appsettings or the environment. Deliberately has no default: passing
    /// <see langword="null"/> is a decision to run on compiled-in defaults, and omitting it by
    /// accident should not compile.
    /// </param>
    /// <param name="configure">
    /// Optional overrides applied after the section, for values the host derives at startup.
    /// </param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddPostgreSqlInfrastructure(
        this IServiceCollection services,
        string connectionString,
        IConfiguration? configuration,
        Action<PostgreSqlConfiguration>? configure = null
    )
        => services.AddPostgreSqlInfrastructure(
            PostgreSqlConfiguration.Resolve(connectionString, configuration, configure));

    /// <summary>
    /// Add PostgreSQL data services from already-resolved options. The single registration path:
    /// every other overload resolves its options through
    /// <see cref="PostgreSqlConfiguration.Resolve"/> and lands here.
    /// </summary>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddPostgreSqlInfrastructure(
        this IServiceCollection services,
        PostgreSqlConfiguration config
    )
    {
        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            throw new ArgumentException(
                "Connection string cannot be null or empty",
                nameof(config)
            );
        }

        // Register interceptors as singletons so caches are shared across all DbContext instances.
        services.TryAddSingleton(new HistoryCommitMargin(TimeSpan.FromMilliseconds(config.HistoryCommitMarginMilliseconds)));
        services.TryAddSingleton<TenantConnectionInterceptor>();
        services.TryAddSingleton<MutationAuditInterceptor>();
        services.TryAddSingleton<PreparedStatementRepairInterceptor>();

        // Audit config cache (singleton — uses IDbContextFactory internally)
        services.TryAddSingleton<ITenantAuditConfigCache, TenantAuditConfigCache>();

        var dataSource = PostgresRuntimeOptions.BuildRuntimeDataSource(config);
        services.AddSingleton(new ContextDataSource(dataSource));
        services.AddSingleton(PostgresRuntimeOptions.BuildRawDataSource(config));

        // Non-pooled: each acquisition is a fresh context, discarded after use. A faulted context
        // is never returned to a pool and reused, so a fault cannot poison later callers. Database
        // connections are pooled by the NpgsqlDataSource singleton.
        services.AddDbContextFactory<NocturneDbContext>(
            (sp, options) =>
            {
                options.UseNpgsql(
                    dataSource,
                    npgsqlOptions =>
                    {
                        npgsqlOptions.EnableRetryOnFailure(
                            maxRetryCount: config.MaxRetryCount,
                            maxRetryDelay: TimeSpan.FromSeconds(config.MaxRetryDelaySeconds),
                            errorCodesToAdd: null
                        );

                        npgsqlOptions.CommandTimeout(config.CommandTimeoutSeconds);
                    }
                );

                if (config.EnableSensitiveDataLogging)
                {
                    options.EnableSensitiveDataLogging();
                }

                if (config.EnableDetailedErrors)
                {
                    options.EnableDetailedErrors();
                }

                options.EnableServiceProviderCaching();
                options.AddInterceptors(
                    sp.GetRequiredService<TenantConnectionInterceptor>(),
                    sp.GetRequiredService<MutationAuditInterceptor>(),
                    sp.GetRequiredService<PreparedStatementRepairInterceptor>());
            }
        );

        // AddDbContextFactory also registers the context itself as scoped. Every context comes from
        // INocturneDbContextFactory instead, so that registration goes.
        services.RemoveAll<NocturneDbContext>();

        // Register the context factory, repositories, and shared services.
        AddDataServices(services);

        return services;
    }

    /// <summary>
    /// Register <see cref="INocturneDbContextFactory"/>, repository
    /// interfaces and deduplication. Called by AddPostgreSqlInfrastructure; also usable independently
    /// by test factories that provide their own IDbContextFactory without creating an NpgsqlDataSource.
    /// </summary>
    public static IServiceCollection AddDataServices(this IServiceCollection services)
    {
        services.AddScoped<ICategoryReadContext>(_ => new CategoryReadContext());
        services.AddScoped(sp => new TenantReadContext(
            sp.GetService<ITenantAccessor>(),
            sp.GetService<ICategoryReadContext>(),
            sp.GetService<IAuditContext>()));
        services.TryAddSingleton(sp => new RootServiceProvider(sp));
        services.TryAddTransient<NocturneDbContextFactory>();
        services.TryAddTransient<INocturneDbContextFactory>(sp => sp.GetRequiredService<NocturneDbContextFactory>());
        services.AddScoped<IReaderCachePartition, ReaderCachePartition>();

        // Register deduplication service (required by repositories)
        services.AddScoped<IDeduplicationService, DeduplicationService>();

        // Register all repositories via their port interfaces
        services.AddScoped<IFoodRepository, FoodRepository>();

        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddTransient<IOAuthTokenRepository, OAuthTokenRepository>();
        services.AddTransient<IOAuthGrantRepository, OAuthGrantRepository>();
        services.AddTransient<IOAuthClientRepository, OAuthClientRepository>();
        services.AddTransient<IMemberInviteRepository, MemberInviteRepository>();
        services.AddTransient<ISubjectRepository, SubjectRepository>();
        services.AddTransient<ITenantRepository, TenantRepository>();
        services.AddTransient<ITenantMembershipRepository, TenantMembershipRepository>();
        services.AddTransient<IOidcProviderRepository, OidcProviderRepository>();
        services.AddTransient<IRoleRepository, RoleRepository>();
        services.AddTransient<IPasskeyCredentialRepository, PasskeyCredentialRepository>();
        services.AddTransient<ITotpCredentialRepository, TotpCredentialRepository>();
        services.AddTransient<IRecoveryCodeRepository, RecoveryCodeRepository>();
        services.AddTransient<IConnectorConfigurationRepository, ConnectorConfigurationRepository>();
        services.AddTransient<IConnectorResetJobRepository, ConnectorResetJobRepository>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddTransient<IJobLedger, JobLedgerRepository>();
        services.AddScoped<SourceOwnedDataRepository>();
        services.AddScoped<ISourceOwnedDataReader>(sp => sp.GetRequiredService<SourceOwnedDataRepository>());
        services.AddScoped<ISourceOwnedDataPurge>(sp => sp.GetRequiredService<SourceOwnedDataRepository>());
        services.AddScoped<IDemoDataPurge, DemoDataPurgeRepository>();

        // Register avatar storage
        services.AddScoped<IAvatarStore, DatabaseAvatarStore>();

        return services;
    }

    /// <summary>
    /// Add discrepancy analysis repository services
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddDiscrepancyAnalysisRepository(
        this IServiceCollection services
    )
    {
        services.AddScoped<IDiscrepancyAnalysisRepository, DiscrepancyAnalysisRepository>();
        return services;
    }

    /// <summary>
    /// Add alert-related repository services
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddAlertRepositories(this IServiceCollection services)
    {
        services.AddScoped<IAlertTrackerRepository, AlertTrackerRepository>();
        services.AddScoped<IAlertRuleRepository, AlertRuleRepository>();
        services.AddScoped<ITrackerRepository, TrackerRepository>();
        services.AddScoped<IStateSpanRepository, StateSpanRepository>();
        services.AddScoped<ISleepSessionRepository, SleepSessionRepository>();
        services.AddScoped<IActivityDedupRepository, ActivityDedupRepository>();
        services.AddScoped<ISystemEventRepository, SystemEventRepository>();
        services.AddScoped<IUserFoodFavoriteRepository, UserFoodFavoriteRepository>();
        services.AddScoped<ITreatmentFoodRepository, TreatmentFoodRepository>();
        return services;
    }

    /// <summary>
    /// Persists Data Protection keys to <see cref="NocturneDbContext"/> so the key ring
    /// survives container restarts (<see cref="NocturneDbXmlRepository"/>). Call this on the builder
    /// returned by <c>services.AddDataProtection()</c>.
    /// </summary>
    public static IDataProtectionBuilder PersistKeysToNocturneDb(
        this IDataProtectionBuilder builder)
    {
        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(services =>
            new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlRepository = new NocturneDbXmlRepository(
                    services.GetRequiredService<IDbContextFactory<NocturneDbContext>>(),
                    services.GetRequiredService<ILogger<NocturneDbXmlRepository>>())));
        return builder;
    }
}
