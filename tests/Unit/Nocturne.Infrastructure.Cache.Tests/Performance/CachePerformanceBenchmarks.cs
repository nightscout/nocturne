using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Extensions;
using Xunit;

namespace Nocturne.Infrastructure.Cache.Tests.Performance;

/// <summary>
/// Performance benchmarks for cache operations to validate performance targets
/// Target: Sub-10ms cache retrieval times, >80% hit rates
/// </summary>
public class CachePerformanceBenchmarks : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ICacheService _cacheService;
    private readonly ServiceProvider _serviceProvider;

    public CachePerformanceBenchmarks(ITestOutputHelper output)
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
    [Trait("Category", "Performance")]
    [Trait("Category", "Cache")]
    public async Task CacheRetrievalPerformance_Should_BeSubTenMilliseconds()
    {
        // Arrange
        const int iterations = 1000;
        var testData = new TestCacheData
        {
            Id = Guid.NewGuid().ToString(),
            Value = "Performance test data",
            Timestamp = DateTimeOffset.UtcNow,
        };

        // Pre-populate cache
        await _cacheService.SetAsync(
            "perf-test",
            testData,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var retrievalTimes = new List<TimeSpan>();
        var stopwatch = new Stopwatch();

        // Act - Measure cache retrieval performance
        for (int i = 0; i < iterations; i++)
        {
            stopwatch.Restart();
            var result = await _cacheService.GetAsync<TestCacheData>(
                "perf-test",
                TestContext.Current.CancellationToken
            );
            stopwatch.Stop();

            retrievalTimes.Add(stopwatch.Elapsed);
            Assert.NotNull(result);
        }

        // Assert - Performance targets
        var averageMs = retrievalTimes.Average(t => t.TotalMilliseconds);
        var medianMs = retrievalTimes
            .OrderBy(t => t.TotalMilliseconds)
            .Skip(iterations / 2)
            .First()
            .TotalMilliseconds;
        var maxMs = retrievalTimes.Max(t => t.TotalMilliseconds);

        _output.WriteLine($"Cache Retrieval Performance Results:");
        _output.WriteLine($"  Average: {averageMs:F2}ms");
        _output.WriteLine($"  Median: {medianMs:F2}ms");
        _output.WriteLine($"  Max: {maxMs:F2}ms");
        _output.WriteLine($"  Iterations: {iterations}");

        // Performance target: Sub-10ms average retrieval time
        Assert.True(
            averageMs < 10.0,
            $"Average cache retrieval time {averageMs:F2}ms exceeds target of 10ms"
        );

        // Additional checks
        Assert.True(
            medianMs < 10.0,
            $"Median cache retrieval time {medianMs:F2}ms exceeds target of 10ms"
        );
    }

    [Fact]
    [Trait("Category", "Cache")]
    public async Task CacheHitRatePerformance_Should_ExceedEightyPercent()
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

        // Assert - Hit rate performance
        var hitRate = (double)cacheHits / totalRequests;
        var hitRatePercentage = hitRate * 100;

        _output.WriteLine($"Cache Hit Rate Performance Results:");
        _output.WriteLine($"  Cache Hits: {cacheHits}");
        _output.WriteLine($"  Cache Misses: {cacheMisses}");
        _output.WriteLine($"  Hit Rate: {hitRatePercentage:F1}%");
        _output.WriteLine($"  Total Requests: {totalRequests}");

        // Performance target: >80% hit rate
        Assert.True(
            hitRate > 0.80,
            $"Cache hit rate {hitRatePercentage:F1}% is below target of 80%"
        );
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Category", "Cache")]
    public async Task CacheSetPerformance_Should_BeReasonablyFast()
    {
        // Arrange
        const int iterations = 500;
        var testData = new TestCacheData
        {
            Id = Guid.NewGuid().ToString(),
            Value = "Performance test data for set operations",
            Timestamp = DateTimeOffset.UtcNow,
        };

        var setTimes = new List<TimeSpan>();
        var stopwatch = new Stopwatch();

        // Act - Measure cache set performance
        for (int i = 0; i < iterations; i++)
        {
            var key = $"set-perf-test-{i}";

            stopwatch.Restart();
            await _cacheService.SetAsync(
                key,
                testData,
                cancellationToken: TestContext.Current.CancellationToken
            );
            stopwatch.Stop();

            setTimes.Add(stopwatch.Elapsed);
        }

        // Assert - Performance analysis
        var averageMs = setTimes.Average(t => t.TotalMilliseconds);
        var medianMs = setTimes
            .OrderBy(t => t.TotalMilliseconds)
            .Skip(iterations / 2)
            .First()
            .TotalMilliseconds;
        var maxMs = setTimes.Max(t => t.TotalMilliseconds);

        _output.WriteLine($"Cache Set Performance Results:");
        _output.WriteLine($"  Average: {averageMs:F2}ms");
        _output.WriteLine($"  Median: {medianMs:F2}ms");
        _output.WriteLine($"  Max: {maxMs:F2}ms");
        _output.WriteLine($"  Iterations: {iterations}");

        // Reasonable performance expectation for set operations
        Assert.True(
            averageMs < 50.0,
            $"Average cache set time {averageMs:F2}ms exceeds reasonable threshold of 50ms"
        );
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Category", "Cache")]
    public async Task BulkOperationPerformance_Should_HandleHighThroughput()
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

        var stopwatch = Stopwatch.StartNew();

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
        stopwatch.Stop();

        // Assert - Throughput performance
        var totalOperations = concurrentOperations * 3; // Set + Get + Update
        var operationsPerSecond = totalOperations / stopwatch.Elapsed.TotalSeconds;

        _output.WriteLine($"Bulk Operation Performance Results:");
        _output.WriteLine($"  Total Operations: {totalOperations}");
        _output.WriteLine($"  Total Time: {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
        _output.WriteLine($"  Operations/Second: {operationsPerSecond:F0}");
        _output.WriteLine($"  Concurrent Workers: {concurrentOperations}");

        // Performance expectation: Should handle reasonable throughput
        Assert.True(
            operationsPerSecond > 100,
            $"Operations per second {operationsPerSecond:F0} is below reasonable threshold"
        );
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }
}

/// <summary>
/// Test data class for performance benchmarks
/// </summary>
public class TestCacheData
{
    public string Id { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
}
