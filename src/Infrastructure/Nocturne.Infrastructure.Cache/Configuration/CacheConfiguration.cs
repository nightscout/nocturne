namespace Nocturne.Infrastructure.Cache.Configuration;

/// <summary>
/// Configuration for in-memory caching
/// </summary>
public class CacheConfiguration
{
    /// <summary>
    /// Key prefix for cache entries
    /// </summary>
    public string KeyPrefix { get; set; } = "nocturne";

    /// <summary>
    /// Default cache expiration in seconds
    /// </summary>
    public int DefaultExpirationSeconds { get; set; } = 300;
}
