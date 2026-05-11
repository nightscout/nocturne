using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Connectors.MyLife.Services;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Services;

public class MyLifeEventsCacheTests
{
    private readonly DateTime _since = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MyLifeEventsCache CreateCache(SpySyncService spy)
    {
        var session = new MyLifeSessionStore();
        session.SetSession("https://example.com", "https://rest.example.com", "token", "user-1", "patient-1");
        return new MyLifeEventsCache(session, spy, NullLogger<MyLifeEventsCache>.Instance);
    }

    [Fact]
    public async Task MultipleCallsWithinSameMinute_DoNotRefetch()
    {
        var spy = new SpySyncService();
        var cache = CreateCache(spy);
        var baseTime = new DateTime(2025, 6, 1, 12, 30, 0, DateTimeKind.Utc);

        await cache.GetEventsAsync(_since, baseTime.AddSeconds(5), CancellationToken.None);
        await cache.GetEventsAsync(_since, baseTime.AddSeconds(30), CancellationToken.None);
        await cache.GetEventsAsync(_since, baseTime.AddSeconds(59), CancellationToken.None);

        spy.CallCount.Should().Be(1, "all calls are within the same minute and should hit the cache");
    }

    [Fact]
    public async Task CallAcrossMinuteBoundary_Refetches()
    {
        var spy = new SpySyncService();
        var cache = CreateCache(spy);
        var firstMinute = new DateTime(2025, 6, 1, 12, 30, 45, DateTimeKind.Utc);
        var nextMinute = new DateTime(2025, 6, 1, 12, 31, 5, DateTimeKind.Utc);

        await cache.GetEventsAsync(_since, firstMinute, CancellationToken.None);
        await cache.GetEventsAsync(_since, nextMinute, CancellationToken.None);

        spy.CallCount.Should().Be(2, "the until crossed a minute boundary so the cache should be invalidated");
    }

    private sealed class SpySyncService() : MyLifeSyncService(null!, NullLogger<MyLifeSyncService>.Instance)
    {
        public int CallCount { get; private set; }

        public override Task<IReadOnlyList<MyLifeEvent>> FetchEventsAsync(
            string serviceUrl, string authToken, string patientId,
            DateTime since, DateTime until, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<MyLifeEvent>>([]);
        }
    }
}
