using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Adapters;
using Nocturne.Infrastructure.Data.Configuration;
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

        // Register NpgsqlDataSource as a singleton - this manages the connection pool
        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(
            postgreSqlConfig.ConnectionString
        );
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        // Register DbContext with PostgreSQL
        services.AddDbContext<NocturneDbContext>(
            (serviceProvider, options) =>
            {
                var config = serviceProvider
                    .GetRequiredService<IOptions<PostgreSqlConfiguration>>()
                    .Value;

                var dataSource = serviceProvider.GetRequiredService<Npgsql.NpgsqlDataSource>();

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
            }
        );

        // Register PostgreSQL service
        services.AddScoped<IPostgreSqlService, PostgreSqlService>();

        // Register PostgreSQL adapter as IDataService for drop-in replacement
        services.AddScoped<IDataService, PostgreSqlDataService>();

        // Register Nightscout query parser
        services.AddScoped<IQueryParser, QueryParser>();

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

        // Register NpgsqlDataSource as a singleton - this manages the connection pool
        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(config.ConnectionString);
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        // Register DbContext with PostgreSQL
        services.AddDbContext<NocturneDbContext>(
            (serviceProvider, options) =>
            {
                var config = serviceProvider
                    .GetRequiredService<IOptions<PostgreSqlConfiguration>>()
                    .Value;

                var dataSource = serviceProvider.GetRequiredService<Npgsql.NpgsqlDataSource>();

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
            }
        );

        // Register PostgreSQL service
        services.AddScoped<IPostgreSqlService, PostgreSqlService>();

        // Register PostgreSQL adapter as IDataService for drop-in replacement
        services.AddScoped<IDataService, PostgreSqlDataService>();

        // Register Nightscout query parser
        services.AddScoped<IQueryParser, QueryParser>();

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
    /// Run database migrations
    /// </summary>
    /// <param name="serviceProvider">Service provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task</returns>
    public static async Task MigrateDatabaseAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<NocturneDbContext>>();

        try
        {
            logger.LogInformation("Running PostgreSQL database migrations");
            await context.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("PostgreSQL database migrations completed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run PostgreSQL database migrations");
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
        services.AddScoped<DiscrepancyAnalysisRepository>();
        return services;
    }

    /// <summary>
    /// Add alert-related repository services
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddAlertRepositories(this IServiceCollection services)
    {
        services.AddScoped<AlertRuleRepository>();
        services.AddScoped<AlertHistoryRepository>();
        services.AddScoped<NotificationPreferencesRepository>();
        services.AddScoped<TrackerRepository>();
        services.AddScoped<StateSpanRepository>();
        services.AddScoped<SystemEventRepository>();
        services.AddScoped<TreatmentFoodRepository>();
        services.AddScoped<UserFoodFavoriteRepository>();
        services.AddScoped<EntryRepository>();
        services.AddScoped<TreatmentRepository>();
        return services;
    }
}
