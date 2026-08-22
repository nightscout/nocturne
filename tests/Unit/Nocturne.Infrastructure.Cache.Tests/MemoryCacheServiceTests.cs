using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Extensions;
using Xunit;

namespace Nocturne.Infrastructure.Cache.Tests;

public class MemoryCacheServiceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ICacheService _cacheService;
    private readonly ServiceProvider _serviceProvider;

    public MemoryCacheServiceTests(ITestOutputHelper output)
    {
        _output = output;

        // Set up in-memory cache for testing
        var services = new ServiceCollection();
        services.AddNocturneMemoryCache();
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
        _cacheService = _serviceProvider.GetRequiredService<ICacheService>();
    }

    [Fact]
    [Trait("Category", "Cache")]
    public async Task CacheHitRate_Should_ExceedEightyPercent()
    {
        // Arrange
        const int totalRequests = 1000;
        const int uniqueKeys = 100; // This gives us a 90% theoretical hit rate after first round
        var random = new Random(42); // Fixed seed for reproducible results

        var testData = Enumerable
            .Range(1, uniqueKeys)
            .Select(i => new TestCacheData
            {
                Id = $"hit-test-{i}",
                Value = $"Test data {i}",
                Timestamp = DateTimeOffset.UtcNow,
            })
            .ToList();

        // Pre-populate cache with some data (simulate real usage)
        for (int i = 0; i < uniqueKeys / 2; i++)
        {
            await _cacheService.SetAsync(
                $"hit-test-{i + 1}",
                testData[i],
                cancellationToken: TestContext.Current.CancellationToken
            );
        }

        int cacheHits = 0;
        int cacheMisses = 0;

        // Act - Simulate realistic cache access patterns
        for (int request = 0; request < totalRequests; request++)
        {
            // Weighted random selection favoring recently used keys (realistic pattern)
            var keyIndex =
                random.NextDouble() < 0.8
                    ? random.Next(1, uniqueKeys / 2 + 1) // 80% chance of frequently used keys
                    : random.Next(1, uniqueKeys + 1); // 20% chance of any key

            var key = $"hit-test-{keyIndex}";
            var result = await _cacheService.GetAsync<TestCacheData>(
                key,
                TestContext.Current.CancellationToken
            );

            if (result != null)
            {
                cacheHits++;
            }
            else
            {
                cacheMisses++;
                // Simulate adding to cache on miss (realistic behavior)
                var dataIndex = keyIndex - 1;
                if (dataIndex < testData.Count)
                {
                    await _cacheService.SetAsync(
                        key,
                        testData[dataIndex],
                        cancellationToken: TestContext.Current.CancellationToken
                    );
                }
            }
        }

        // Assert - Hit rate behaviour
        var hitRate = (double)cacheHits / totalRequests;
        var hitRatePercentage = hitRate * 100;

        _output.WriteLine($"Cache Hit Rate Results:");
        _output.WriteLine($"  Cache Hits: {cacheHits}");
        _output.WriteLine($"  Cache Misses: {cacheMisses}");
        _output.WriteLine($"  Hit Rate: {hitRatePercentage:F1}%");
        _output.WriteLine($"  Total Requests: {totalRequests}");

        // Target: >80% hit rate
        Assert.True(
            hitRate > 0.80,
            $"Cache hit rate {hitRatePercentage:F1}% is below target of 80%"
        );
    }

    [Fact]
    [Trait("Category", "Cache")]
    public async Task ConcurrentReadModifyWrite_Should_LeaveEveryKeyAtItsUpdatedValue()
    {
        // Arrange
        const int concurrentOperations = 100;
        var tasks = new List<Task>();
        var testData = new TestCacheData
        {
            Id = Guid.NewGuid().ToString(),
            Value = "Bulk operation test data",
            Timestamp = DateTimeOffset.UtcNow,
        };

        // Act - Concurrent cache operations
        for (int i = 0; i < concurrentOperations; i++)
        {
            var index = i; // Capture for closure
            tasks.Add(
                Task.Run(
                    async () =>
                    {
                        var key = $"bulk-test-{index}";

                        // Set data
                        await _cacheService.SetAsync(
                            key,
                            testData,
                            cancellationToken: TestContext.Current.CancellationToken
                        );

                        // Read it back
                        var result = await _cacheService.GetAsync<TestCacheData>(
                            key,
                            TestContext.Current.CancellationToken
                        );
                        Assert.NotNull(result);

                        // Update it
                        var updatedData = new TestCacheData
                        {
                            Id = result.Id,
                            Value = $"Updated {result.Value}",
                            Timestamp = DateTimeOffset.UtcNow,
                        };
                        await _cacheService.SetAsync(
                            key,
                            updatedData,
                            cancellationToken: TestContext.Current.CancellationToken
                        );
                    },
                    TestContext.Current.CancellationToken
                )
            );
        }

        await Task.WhenAll(tasks);

        // Assert - every worker's own key survived the others' traffic
        for (int i = 0; i < concurrentOperations; i++)
        {
            var stored = await _cacheService.GetAsync<TestCacheData>(
                $"bulk-test-{i}",
                TestContext.Current.CancellationToken
            );

            Assert.NotNull(stored);
            Assert.Equal($"Updated {testData.Value}", stored.Value);
        }
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }
}

/// <summary>
/// Test data class for cache tests
/// </summary>
public class TestCacheData
{
    public string Id { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
}
