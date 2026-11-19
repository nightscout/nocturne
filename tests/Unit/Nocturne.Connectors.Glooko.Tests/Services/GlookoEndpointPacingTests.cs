using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Glooko.Configurations;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// Glooko's v2 endpoints are fetched one after another with a pause between them, never before the
/// first. The pause runs on the connector's clock, so a test advances it instead of waiting.
/// </summary>
public class GlookoEndpointPacingTests
{
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(5);

    private static readonly string[] V2Endpoints =
    [
        GlookoConstants.FoodsPath, GlookoConstants.ScheduledBasalsPath, GlookoConstants.NormalBolusesPath,
        GlookoConstants.CgmReadingsPath, GlookoConstants.MeterReadingsPath, GlookoConstants.SuspendBasalsPath,
        GlookoConstants.TemporaryBasalsPath,
    ];

    private static readonly SyncDataType[] V2BatchTypes =
    [
        SyncDataType.Glucose, SyncDataType.ManualBG, SyncDataType.Boluses, SyncDataType.CarbIntake,
        SyncDataType.Food, SyncDataType.StateSpans, SyncDataType.TempBasals,
    ];

    [Fact]
    public async Task SyncDataAsync_FetchesTheFirstEndpointAtOnceAndPausesBeforeEachLater()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handler = new V2RecordingHandler(clock);
        var service = Service(handler, clock);

        var sync = service.SyncDataAsync(Request(), GlookoSyncHarness.Config(useV3Api: false), CancellationToken.None);

        await WaitForRequestsAsync(handler, 1);
        await Task.Delay(200);
        handler.Fetched.Should().HaveCount(1, "the clock has not moved, so the pause before the second endpoint holds");

        while (!sync.IsCompleted)
        {
            clock.Advance(Pause);
            await Task.Delay(10);
        }

        await sync;
        handler.Fetched.Select(fetch => fetch.Path).Should().Equal(V2Endpoints);
        handler.Fetched.Zip(handler.Fetched.Skip(1), (previous, next) => next.At - previous.At)
            .Should().OnlyContain(gap => gap >= Pause);
    }

    [Fact]
    public async Task SyncDataAsync_CancelledDuringThePause_StopsAtOnceAndReportsATerminalPhase()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handler = new V2RecordingHandler(clock);
        var progress = new RecordingProgressReporter();
        var service = Service(handler, clock);
        using var cts = new CancellationTokenSource();

        var sync = service.SyncDataAsync(
            Request(), GlookoSyncHarness.Config(useV3Api: false), cts.Token, progress);

        await WaitForRequestsAsync(handler, 1);
        await Task.Delay(200);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => sync.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        handler.Fetched.Should().HaveCount(1, "the pause was cut short and nothing after it ran");
        progress.Phases.Should().EndWith(SyncPhase.Failed);
    }

    private static RecordingGlookoConnectorService Service(V2RecordingHandler handler, TimeProvider clock) =>
        new(new HttpClient(handler), new StaticGlookoTokenProvider(clock), rejected: null, clock: clock,
            endpointPause: Pause);

    private static SyncRequest Request() => new()
    {
        DataTypes = [.. V2BatchTypes],
        From = DateTime.UtcNow.AddDays(-3),
    };

    private static async Task WaitForRequestsAsync(V2RecordingHandler handler, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.Fetched.Count < count)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the sync should have reached its first endpoint");
            await Task.Delay(10);
        }
    }

    private sealed class V2RecordingHandler(TimeProvider clock) : DelegatingHandler(new GlookoEndpointHandler())
    {
        private readonly Lock _gate = new();
        private readonly List<(string Path, DateTimeOffset At)> _fetched = [];

        public IReadOnlyList<(string Path, DateTimeOffset At)> Fetched
        {
            get { lock (_gate) return [.. _fetched]; }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (V2Endpoints.Contains(path, StringComparer.OrdinalIgnoreCase))
                lock (_gate) _fetched.Add((path, clock.GetUtcNow()));

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingProgressReporter : ISyncProgressReporter
    {
        private readonly Lock _gate = new();
        private readonly List<SyncPhase> _phases = [];

        public IReadOnlyList<SyncPhase> Phases
        {
            get { lock (_gate) return [.. _phases]; }
        }

        public Task ReportProgressAsync(SyncProgressEvent progress, CancellationToken ct = default)
        {
            lock (_gate) _phases.Add(progress.Phase);
            return Task.CompletedTask;
        }
    }
}
