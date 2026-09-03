using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Cache.Services;

namespace Nocturne.Infrastructure.Cache.Extensions;

/// <summary>
/// Service collection extensions for cache registration
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Nocturne cache services with in-memory caching (recommended for single-user deployments)
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddNocturneMemoryCache(
        this IServiceCollection services
    )
    {
        // Add in-memory cache
        services.AddMemoryCache();

        // Configure cache settings
        services.Configure<CacheConfiguration>(options =>
        {
            options.KeyPrefix = "nocturne";
            options.DefaultExpirationSeconds = 300;
        });

        // Register in-memory cache service
        services.AddSingleton<ICacheService, MemoryCacheService>();

        // Register processing status service (in-memory)
        services.AddSingleton<IProcessingStatusService, MemoryProcessingStatusService>();

        return services;
    }
}
