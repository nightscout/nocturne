using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Storage;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Service collection extensions for PostgreSQL data infrastructure
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add PostgreSQL data services to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddPostgreSqlInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // Register configuration
        var configSection = configuration.GetSection(PostgreSqlConfiguration.SectionName);
        services.Configure<PostgreSqlConfiguration>(configSection);

        var postgreSqlConfig =
            configSection.Get<PostgreSqlConfiguration>() ?? new PostgreSqlConfiguration();

        // Validate configuration
        if (string.IsNullOrEmpty(postgreSqlConfig.ConnectionString))
        {
            throw new InvalidOperationException(
                "PostgreSQL connection string must be provided in configuration section 'PostgreSql:ConnectionString'"
            );
        }

        // Register interceptors as singletons so caches are shared across all DbContext instances.
        services.TryAddSingleton<TenantConnectionInterceptor>();
        services.TryAddSingleton<MutationAuditInterceptor>();

        // Audit config cache (singleton — uses IDbContextFactory internally)
        services.TryAddSingleton<ITenantAuditConfigCache, TenantAuditConfigCache>();

        // Register NpgsqlDataSource as a singleton - this manages the connection pool
        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(
            postgreSqlConfig.ConnectionString
        );
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        // Use AddPooledDbContextFactory for multitenant context pooling
        services.AddPooledDbContextFactory<NocturneDbContext>(
            (sp, options) =>
            {
                options.UseNpgsql(
                    dataSource,
                    npgsqlOptions =>
                    {
                        npgsqlOptions.EnableRetryOnFailure(
                            maxRetryCount: postgreSqlConfig.MaxRetryCount,
                            maxRetryDelay: TimeSpan.FromSeconds(postgreSqlConfig.MaxRetryDelaySeconds),
                            errorCodesToAdd: null
                        );

                        npgsqlOptions.CommandTimeout(postgreSqlConfig.CommandTimeoutSeconds);
                    }
                );

                if (postgreSqlConfig.EnableSensitiveDataLogging)
                {
                    options.EnableSensitiveDataLogging();
                }

                if (postgreSqlConfig.EnableDetailedErrors)
                {
                    options.EnableDetailedErrors();
                }

                options.EnableServiceProviderCaching();
                options.AddInterceptors(
                    sp.GetRequiredService<TenantConnectionInterceptor>(),
                    sp.GetRequiredService<MutationAuditInterceptor>());
            },
            poolSize: 128
        );

        // Register scoped NocturneDbContext that sets TenantId from ITenantAccessor.
        // All existing constructor injections of NocturneDbContext continue to work.
        // The context is returned to the pool when the scope ends.
        services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
            var context = factory.CreateDbContext();
            var tenantAccessor = sp.GetService<ITenantAccessor>();
            if (tenantAccessor?.IsResolved == true)
            {
                context.TenantId = tenantAccessor.TenantId;
            }
            return context;
        });

        // Register deduplication service (required by repositories)
        services.AddScoped<IDeduplicationService, DeduplicationService>();

        // Register all repositories via their port interfaces
        services.AddScoped<IFoodRepository, FoodRepository>();

        services.AddScoped<ISettingsRepository, SettingsRepository>();

        // Register Nightscout query parser
        services.AddScoped<IQueryParser, QueryParser>();

        // Register avatar storage
        services.AddScoped<IAvatarStore, DatabaseAvatarStore>();

        return services;
    }

    /// <summary>
    /// Add PostgreSQL data services with explicit connection string
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddPostgreSqlInfrastructure(
        this IServiceCollection services,
        string connectionString,
        Action<PostgreSqlConfiguration>? configure = null
    )
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new ArgumentException(
                "Connection string cannot be null or empty",
                nameof(connectionString)
            );
        }

        // Create and configure options
        var config = new PostgreSqlConfiguration { ConnectionString = connectionString };
        configure?.Invoke(config);

        // Validate connection string is still set after configure action
        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            throw new InvalidOperationException(
                "Connection string was cleared by the configure action"
            );
        }

        // Register configuration
        services.Configure<PostgreSqlConfiguration>(options =>
        {
            options.ConnectionString = config.ConnectionString;
            options.EnableSensitiveDataLogging = config.EnableSensitiveDataLogging;
            options.EnableDetailedErrors = config.EnableDetailedErrors;
            options.MaxRetryCount = config.MaxRetryCount;
            options.MaxRetryDelaySeconds = config.MaxRetryDelaySeconds;
            options.CommandTimeoutSeconds = config.CommandTimeoutSeconds;
        });

        // Register interceptors as singletons so caches are shared across all DbContext instances.
        services.TryAddSingleton<TenantConnectionInterceptor>();
        services.TryAddSingleton<MutationAuditInterceptor>();

        // Audit config cache (singleton — uses IDbContextFactory internally)
        services.TryAddSingleton<ITenantAuditConfigCache, TenantAuditConfigCache>();

        // Register NpgsqlDataSource as a singleton - this manages the connection pool
        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(config.ConnectionString);
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        // Use AddPooledDbContextFactory for multitenant context pooling
        services.AddPooledDbContextFactory<NocturneDbContext>(
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
                    sp.GetRequiredService<MutationAuditInterceptor>());
            },
            poolSize: 128
        );

        // Register scoped DbContext, repositories, and shared services.
        AddDataServices(services);

        return services;
    }

    /// <summary>
    /// Register scoped NocturneDbContext, repository interfaces, deduplication, and query parser.
    /// Called by AddPostgreSqlInfrastructure; also usable independently by test factories
    /// that provide their own IDbContextFactory without creating an NpgsqlDataSource.
    /// </summary>
    public static IServiceCollection AddDataServices(this IServiceCollection services)
    {
        // Register scoped NocturneDbContext that sets TenantId from ITenantAccessor.
        // All existing constructor injections of NocturneDbContext continue to work.
        // The context is returned to the pool when the scope ends.
        services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
            var context = factory.CreateDbContext();
            var tenantAccessor = sp.GetService<ITenantAccessor>();
            if (tenantAccessor?.IsResolved == true)
            {
                context.TenantId = tenantAccessor.TenantId;
            }
            return context;
        });

        // Register deduplication service (required by repositories)
        services.AddScoped<IDeduplicationService, DeduplicationService>();

        // Register all repositories via their port interfaces
        services.AddScoped<IFoodRepository, FoodRepository>();

        services.AddScoped<ISettingsRepository, SettingsRepository>();

        // Register Nightscout query parser
        services.AddScoped<IQueryParser, QueryParser>();

        // Register avatar storage
        services.AddScoped<IAvatarStore, DatabaseAvatarStore>();

        return services;
    }

    /// <summary>
    /// Ensure the database is created and up to date
    /// </summary>
    /// <param name="serviceProvider">Service provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task</returns>
    public static async Task EnsureDatabaseCreatedAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<NocturneDbContext>>();

        try
        {
            logger.LogInformation("Ensuring PostgreSQL database is created and up to date");
            await context.Database.EnsureCreatedAsync(cancellationToken);
            logger.LogInformation("PostgreSQL database is ready");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to ensure PostgreSQL database is created");
            throw;
        }
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
        services.AddScoped<ITrackerRepository, TrackerRepository>();
        services.AddScoped<IStateSpanRepository, StateSpanRepository>();
        services.AddScoped<ISystemEventRepository, SystemEventRepository>();
        services.AddScoped<IUserFoodFavoriteRepository, UserFoodFavoriteRepository>();
        services.AddScoped<ITreatmentFoodRepository, TreatmentFoodRepository>();
        return services;
    }
}
