using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Infrastructure.Cache.Constants;
using Nocturne.Infrastructure.Cache.Services;
using Xunit;

namespace Nocturne.Infrastructure.Cache.Tests;

/// <summary>
/// The processing-status TTL, the background sweep and the completion poll are all clock-driven,
/// on intervals (an hour, five minutes, a ten-minute timeout) no test can wait out. Driving them
/// from <see cref="FakeTimeProvider"/> asserts each boundary at the exact tick instead.
/// </summary>
public class MemoryProcessingStatusServiceTimeTests
{
    private static readonly TimeSpan Ttl = CacheConstants.DefaultTtl.ProcessingStatus;
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (MemoryProcessingStatusService Service, FakeTimeProvider Time, CapturingLogger Log) Build()
    {
        var time = new FakeTimeProvider(Origin);
        var log = new CapturingLogger();
        return (new MemoryProcessingStatusService(log, time), time, log);
    }

    [Fact]
    [Trait("Category", "Cache")]
    public async Task GetStatus_Should_SurviveTheExactTtlInstant()
    {
        var (service, time, _) = Build();
        await service.InitializeAsync("run", 1, TestContext.Current.CancellationToken);

        time.Advance(Ttl);

        var status = await service.GetStatusAsync("run", TestContext.Current.CancellationToken);

        status.Should().NotBeNull("an entry expires strictly after StartedAt + TTL, not on it");
    }

    [Fact]
    [Trait("Category", "Cache")]
    public async Task GetStatus_Should_ExpireOneTickPastTheTtl()
    {
        var (service, time, _) = Build();
        await service.InitializeAsync("run", 1, TestContext.Current.CancellationToken);

        time.Advance(Ttl + TimeSpan.FromTicks(1));

        var status = await service.GetStatusAsync("run", TestContext.Current.CancellationToken);

        status.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Cache")]
    public async Task Timestamps_Should_ComeFromTheInjectedClock()
    {
        var (service, time, _) = Build();
        await service.InitializeAsync("run", 1, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(3));
        await service.MarkCompletedAsync("run", null, TestContext.Current.CancellationToken);

        var status = await service.GetStatusAsync("run", TestContext.Current.CancellationToken);

        status!.StartedAt.Should().Be(Origin.UtcDateTime);
        status.StartedAt.Kind.Should().Be(DateTimeKind.Utc);
        status.CompletedAt.Should().Be(Origin.UtcDateTime.AddMinutes(3));
    }

    [Fact]
    [Trait("Category", "Cache")]
    public async Task CleanupSweep_Should_RunOnTheInjectedClock()
    {
        var (service, time, log) = Build();
        await service.InitializeAsync("run", 1, TestContext.Current.CancellationToken);

        // Past the TTL, so the next sweep tick has something to drop.
        time.Advance(Ttl + CacheConstants.CleanupIntervals.StatusCleanup);

        log.Messages.Should().Contain(m => m.Contains("Cleaned up 1 expired processing status entries"));
    }

    [Fact]
    [Trait("Category", "Cache")]
    public async Task WaitForCompletion_Should_TimeOutOnTheInjectedClock()
    {
        var (service, time, _) = Build();
        await service.InitializeAsync("run", 1, TestContext.Current.CancellationToken);

        var wait = service.WaitForCompletionAsync("run", TimeSpan.FromMinutes(10), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(10));

        var result = await wait.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Cache")]
    public async Task WaitForCompletion_Should_ReturnAsSoonAsTheRunCompletes()
    {
        var (service, time, _) = Build();
        await service.InitializeAsync("run", 1, TestContext.Current.CancellationToken);

        var wait = service.WaitForCompletionAsync("run", TimeSpan.FromMinutes(10), TestContext.Current.CancellationToken);
        await service.MarkCompletedAsync("run", null, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));

        var result = await wait.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result!.Status.Should().Be(CacheConstants.ProcessingStatus.Completed);
    }

    private sealed class CapturingLogger : ILogger<MemoryProcessingStatusService>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages) return _messages.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_messages) _messages.Add(formatter(state, exception));
        }
    }
}
