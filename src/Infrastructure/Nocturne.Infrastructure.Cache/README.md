# Nocturne Redis Caching Infrastructure

The Nocturne caching infrastructure provides a comprehensive Redis-based caching layer optimized for diabetes management
data. Redis is required for all environments to ensure consistent behavior and proper distributed cache functionality.

## Features

### 🚀 Core Capabilities

- **In-Memory Caching**: Optimized memory cache for single-user deployments
- **Pattern-Based Invalidation**: Regex pattern matching support for cache keys
- **Cache Warming**: Proactive preloading of frequently accessed data
- **Complex Invalidation**: Chain-based cache invalidation for related data
- **Performance Optimized**: Sub-10ms retrieval times, >80% hit rates
- **Error Resilience**: Error handling with graceful degradation
- **Timeout Protection**: Factory function timeout protection in GetOrSet operations

### 🩺 Diabetes Data Optimization

Specialized caching patterns for:

- **Glucose Readings**: Current entries, recent trends, statistics
- **User Profiles**: Settings, preferences, configuration
- **Treatments**: Recent insulin, carbs, medications
- **Calculations**: IOB, COB, time-in-range statistics
- **System Data**: Lookups, status, metadata

## Quick Start

### 1. Basic Setup

```csharp
// Add to Program.cs or Startup.cs
builder.Services.AddNocturneCache(builder.Configuration);

// Optional: Add configuration validation (recommended)
builder.Services.ValidateNocturneCacheConfiguration(builder.Configuration, throwOnValidationErrors: false);

// Or for Redis-specific setup
builder.Services.AddNocturneRedisCache("localhost:6379");

// For .NET Aspire integration
builder.Services.AddNocturneAspireCache();
```

### 2. Configuration

Add to `appsettings.json`:

```json
{
  "RedisCache": {
    "ConnectionString": "localhost:6379",
    "Database": 0,
    "KeyPrefix": "nocturne",
    "DefaultExpirationSeconds": 300,
    "CurrentEntryExpirationSeconds": 60,
    "RecentEntriesExpirationSeconds": 120,
    "RecentTreatmentsExpirationSeconds": 300,
    "ProfileTimestampExpirationSeconds": 1800,
    "EnableDistributedCache": true,
    "EnableBackgroundCacheRefresh": false
  }
}
```

### 3. Add Health Checks

```csharp
// Health checks are automatically registered
// Access at: /health
app.MapHealthChecks("/health");
```

## Usage Examples

### Basic Cache Operations

```csharp
public class DiabetesDataService
{
    private readonly ICacheService _cacheService;
    private readonly IMongoDbService _dbService;

    public DiabetesDataService(ICacheService cacheService, IMongoDbService dbService)
    {
        _cacheService = cacheService;
        _dbService = dbService;
    }

    public async Task<Entry?> GetCurrentEntryAsync()
    {
        return await _cacheService.GetOrSetAsync(
            "entries:current",
            () => _dbService.GetCurrentEntryAsync(),
            TimeSpan.FromSeconds(60)
        );
    }

    public async Task CreateEntryAsync(Entry entry)
    {
        await _dbService.CreateEntryAsync(entry);

        // Invalidate related caches
        await _cacheService.RemoveAsync("entries:current");
        await _cacheService.RemoveByPatternAsync("entries:recent:*");
    }
}
```

### Cache Warming

```csharp
public class StartupService : IHostedService
{
    private readonly ICacheWarmingService _cacheWarming;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Warm system-wide cache
        await _cacheWarming.WarmSystemCacheAsync(cancellationToken);

        // Warm user-specific cache for active users
        await _cacheWarming.WarmUserCacheAsync("user123", cancellationToken);
    }
}
```

### Complex Cache Invalidation

```csharp
public class TreatmentService
{
    private readonly ICacheInvalidationService _invalidation;

    public async Task AddInsulinTreatmentAsync(Treatment treatment)
    {
        await _dbService.CreateTreatmentAsync(treatment);

        // Automatically invalidates:
        // - treatments:recent:*
        // - calculations:iob:*
        // - stats:*
        await _invalidation.InvalidateForNewInsulinTreatmentAsync(treatment.UserId);
    }
}
```

## Cache Key Patterns

The system uses consistent, hierarchical cache keys for diabetes data:

```
glucose:{userId}:recent           # Last 24 hours glucose readings
glucose:{userId}:trends          # Calculated trends and statistics
user:{userId}:profile            # User profile information
user:{userId}:settings           # User preferences and settings
system:lookups                   # System-wide lookup data
entries:current                  # Current entry cache
entries:recent:{userId}:{count}  # Recent entries with filters
treatments:recent:{userId}:{hours} # Recent treatments by time
calculations:iob:{userId}:{timestamp} # IOB calculation results
calculations:cob:{userId}:{timestamp} # COB calculation results
stats:glucose:{userId}:{period}  # Glucose statistics
profiles:at:{userId}:{timestamp} # Profile at specific time
```

## Cache Policies & TTL

Optimized expiration times based on data usage patterns:

| Data Type                | TTL        | Reason                    |
|--------------------------|------------|---------------------------|
| **Current Entries**      | 1 minute   | High frequency updates    |
| **Recent Entries**       | 2 minutes  | Sliding window data       |
| **Recent Treatments**    | 5 minutes  | Moderate update frequency |
| **User Profiles**        | 30 minutes | Infrequent changes        |
| **User Settings**        | 1 hour     | Very infrequent changes   |
| **System Lookups**       | 4 hours    | Static reference data     |
| **IOB/COB Calculations** | 15 minutes | Expensive computations    |
| **Statistics**           | 30 minutes | Aggregated data           |

## Performance Targets

The caching system is designed to meet specific performance goals:

- **Sub-10ms Retrieval**: Average cache retrieval times under 10ms
- **>80% Hit Rates**: Cache hit rates exceeding 80% for frequently accessed data
- **High Throughput**: Support for concurrent operations
- **Circuit Breaker**: Graceful degradation when Redis is unavailable

## Development Environment

### Docker Compose Setup

```yaml
services:
  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
    command: redis-server --appendonly yes
    volumes:
      - redis_data:/data

  nocturne-api:
    build: .
    environment:
      RedisCache__ConnectionString: redis:6379
      RedisCache__EnableDistributedCache: true
    depends_on:
      - redis

volumes:
  redis_data:
```

### .NET Aspire Integration

The cache infrastructure supports .NET Aspire for orchestrated development:

```csharp
// In AppHost project
builder.AddRedis("cache")
       .WithDataVolume();

// In API project - automatic service discovery
builder.Services.AddNocturneAspireCache();
```

## Enhanced Features (v2.0)

### 🔧 Configuration Validation

Comprehensive configuration validation with detailed error reporting:

```csharp
// Add validation during startup
builder.Services.ValidateNocturneCacheConfiguration(
    builder.Configuration,
    throwOnValidationErrors: true // Set to false for warnings only
);

// Direct validation for testing
var result = ServiceCollectionExtensions.ValidateCacheConfiguration(configuration);
if (!result.IsValid)
{
    Console.WriteLine($"Configuration errors: {string.Join(", ", result.Errors)}");
}
```

### 🎯 Enhanced In-Memory Cache

The memory cache now supports advanced features:

- **Pattern-Based Removal**: Regex pattern matching for cache key invalidation
- **Hit/Miss Tracking**: Real-time statistics collection
- **Automatic Cleanup**: Background cleanup of expired entries
- **Thread-Safe Operations**: Concurrent access with proper locking

```csharp
// Pattern-based cache invalidation now works in memory cache
await cacheService.RemoveByPatternAsync("entries:recent:*");
await cacheService.RemoveByPatternAsync("user:123:*");

// Get enhanced statistics
var stats = await cacheService.GetStatisticsAsync();
Console.WriteLine($"Hit Rate: {stats.HitRate:P2}");
Console.WriteLine($"Total Keys: {stats.TotalKeys}");
```

### ⚡ Performance Improvements

- **Timeout Protection**: Factory functions in GetOrSet operations have 30-second timeout
- **Fire-and-Forget Caching**: Cache writes don't block response times
- **Enhanced Error Handling**: Graceful degradation when cache is unavailable
- **Optimized Configuration**: Consolidated configuration creation reduces memory allocation

### 🏥 Improved Health Checks

Enhanced health check system with better diagnostics:

```csharp
// Health checks now provide detailed information
// - Redis connectivity status
// - Read/write operation verification
// - Connection pool health
// - Memory usage tracking
```

### 🔧 Constants and Maintainability

- **Centralized Constants**: All magic strings moved to `CacheConstants` class
- **Consistent Naming**: Standardized health check names and tags
- **Better Service Lifetimes**: Optimized singleton/scoped registrations

## Architecture

### Service Structure

```
├── Abstractions/
│   ├── ICacheService.cs              # Core cache interface
│   └── CacheItem.cs                  # Cache item metadata
├── Services/
│   ├── RedisCacheService.cs          # Redis implementation
│   ├── MemoryCacheService.cs         # In-memory fallback
│   ├── CacheInvalidationService.cs   # Complex invalidation
│   └── CacheWarmingService.cs        # Proactive warming
├── Configuration/
│   └── RedisCacheConfiguration.cs    # Configuration options
├── HealthChecks/
│   └── RedisHealthCheck.cs           # Redis connectivity check
├── Keys/
│   └── CacheKeyBuilder.cs            # Consistent key patterns
└── Extensions/
    └── ServiceCollectionExtensions.cs # DI registration
```

### Integration Points

- **PostgreSQL Services**: Automatic cache-aside pattern
- **SignalR Hubs**: Real-time cache invalidation
- **Health Checks**: Cache connectivity monitoring
- **Background Services**: Cache warming and refresh

## Testing

### Unit Tests

```bash
# Run cache-specific tests
dotnet test --filter "Category=Cache"

# Run performance benchmarks
dotnet test --filter "Category=Performance"
```

### Performance Benchmarks

The system includes comprehensive performance tests:

- **Cache Retrieval Performance**: Validates sub-10ms targets
- **Hit Rate Performance**: Ensures >80% hit rates
- **Throughput Testing**: Concurrent operation handling
- **Memory Usage**: Resource consumption analysis

## Monitoring & Observability

### Health Checks

Cache health is monitored through:

- Redis connectivity tests
- Read/write operation verification
- Connection pool status
- Memory usage tracking

### Logging

Structured logging for:

- Cache hits/misses
- Performance metrics
- Error conditions
- Invalidation events

### Metrics

Key metrics exposed:

- Hit/miss ratios
- Response times
- Error rates
- Memory usage
- Connection status

## Production Considerations

### Redis Configuration

```
# Recommended Redis configuration
maxmemory 2gb
maxmemory-policy allkeys-lru
appendonly yes
appendfsync everysec
tcp-keepalive 300
timeout 0
```

### Scaling

- **Redis Cluster**: For horizontal scaling
- **Read Replicas**: For read-heavy workloads
- **Connection Pooling**: Managed automatically
- **Circuit Breakers**: Built-in resilience

### Security

- Redis AUTH support
- TLS/SSL connections
- Network isolation
- Key prefix isolation

## Troubleshooting

### Common Issues

**Cache Misses**: Check TTL settings and invalidation patterns
**High Memory Usage**: Review data sizes and expiration policies
**Connection Issues**: Verify Redis connectivity and configuration
**Performance**: Use built-in benchmarks and monitoring

### Debug Tools

```csharp
// Get cache statistics
var stats = await _cacheService.GetStatisticsAsync();

// Check specific key existence
var exists = await _cacheService.ExistsAsync("key");

// Manual cache warming
await _cacheWarming.WarmUserCacheAsync("userId");
```

## Contributing

When adding new cache patterns:

1. Use consistent key patterns from `CacheKeyBuilder`
2. Add appropriate TTL policies
3. Include invalidation logic
4. Add performance tests
5. Update documentation

## License

Part of the Nocturne project - see main project license.
