using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Models.Demo;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Logging;
using Xunit;

namespace Nocturne.Services.Demo.Tests.Services;

public class DemoDataHostedServiceTimeoutTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string Entry = "POST /api/v4/glucose/sensor/bulk";
    private const string Reset = "POST /api/v4/admin/demo/reset";
    private const string StatusUpdate = "PATCH /api/v4/admin/demo/status";
    private const string PersonSeed = "POST /api/v4/admin/demo/seed-extras (person)";

    [Fact]
    public async Task A_realtime_post_that_times_out_is_logged_and_the_next_tick_runs()
    {
        var api = new TimeoutApi(Now) { TimeOutOn = (call, nth) => call == Entry && nth == 2 };

        await using var run = await RunAsync(api, new DemoModeConfiguration());
        await run.AdvanceUntilAsync("the tick after the timed out one", () => api.CountOf(Entry) >= 3);

        run.Service.State.Should().Be(DemoServiceState.Running);
        run.Logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error && e.Exception is TaskCanceledException && e.Message == "Error generating demo data");
    }

    [Fact]
    public async Task A_reset_that_times_out_is_retried_a_reset_interval_later_and_the_realtime_stream_goes_on()
    {
        var api = new TimeoutApi(Now) { LastResetAt = Now.AddMinutes(-59), TimeOutOn = (call, nth) => call == Reset && nth == 1 };

        await using var run = await RunAsync(api, new DemoModeConfiguration { ResetIntervalMinutes = 60 });
        await run.AdvanceUntilAsync("the reset after the timed out one", () => api.CountOf(Reset) >= 2);
        await run.AdvanceUntilAsync("a tick after the reset", () => api.CountOf(Entry) >= 2);

        run.Service.State.Should().Be(DemoServiceState.Running);
        api.CalledAt(Reset)[1].Should().BeOnOrAfter(api.CalledAt(Reset)[0].AddMinutes(60));
    }

    [Fact]
    public async Task A_reading_that_times_out_in_the_tick_of_a_reset_does_not_lose_the_seeding_of_the_people()
    {
        var api = new TimeoutApi(Now)
        {
            LastResetAt = Now.AddMinutes(-59),
            ResetTakes = TimeSpan.FromMinutes(2),
            TimeOutOn = (call, nth) => call == Entry && nth == 2,
        };
        var people = DemoClinic.People.Count;

        await using var run = await RunAsync(api, new DemoModeConfiguration { ResetIntervalMinutes = 60, IntervalMinutes = 1 });
        await run.AdvanceUntilAsync("the people seeded for the start", () => api.CountOf(PersonSeed) >= people);
        await run.AdvanceUntilAsync("the reset and the timed out reading of its tick", () => api.CountOf(Reset) >= 1 && api.CountOf(Entry) >= 2);
        await Eventually.UntilAsync(
            "the people seeded again after the reset",
            () => api.CountOf(PersonSeed) >= 2 * people,
            patience: TimeSpan.FromSeconds(5));

        run.Logger.Errors.Should().Equal("Error generating demo data");
    }

    [Theory]
    [InlineData(Reset)]
    [InlineData(StatusUpdate)]
    public async Task A_start_whose_call_times_out_still_reaches_the_loop_and_posts_readings(string timesOut)
    {
        var api = new TimeoutApi(Now) { TimeOutOn = (call, _) => call == timesOut };

        await using var run = await RunAsync(
            api, new DemoModeConfiguration { ClearOnStartup = true, ResetIntervalMinutes = 60 });
        await run.AdvanceUntilAsync("a tick from the loop", () => api.CountOf(Entry) >= 2);

        run.Service.State.Should().Be(DemoServiceState.Running);
        run.Logger.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_first_reading_that_times_out_is_logged_and_the_loop_posts_the_next()
    {
        var api = new TimeoutApi(Now) { TimeOutOn = (call, nth) => call == Entry && nth == 1 };

        await using var run = await RunAsync(api, new DemoModeConfiguration());
        await run.AdvanceUntilAsync("a tick from the loop", () => api.CountOf(Entry) >= 2);

        run.Service.State.Should().Be(DemoServiceState.Running);
        run.Logger.Errors.Should().ContainSingle().Which.Should().Be("Failed to post the first demo entry");
    }

    [Fact]
    public async Task A_failed_tick_is_logged_once()
    {
        var api = new TimeoutApi(Now) { TimeOutOn = (call, nth) => call == Entry && nth == 2 };

        await using var run = await RunAsync(api, new DemoModeConfiguration());
        await run.AdvanceUntilAsync("the tick after the failed one", () => api.CountOf(Entry) >= 3);

        run.Logger.Errors.Should().Equal("Error generating demo data");
    }

    [Fact]
    public async Task A_tick_that_fails_at_once_is_not_tried_again_before_the_interval()
    {
        var api = new TimeoutApi(Now) { RefuseOn = (call, nth) => call == Entry && nth == 2 };

        await using var run = await RunAsync(api, new DemoModeConfiguration());
        await run.AdvanceUntilAsync("the refused tick", () => api.CountOf(Entry) >= 2);

        run.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        api.CountOf(Entry).Should().Be(2, "the retry waits for the generation interval");

        await run.AdvanceUntilAsync("the retry", () => api.CountOf(Entry) >= 3);
    }

    [Fact]
    public void A_step_that_was_due_when_a_tick_failed_is_deferred_and_one_not_yet_due_is_kept()
    {
        var retryAt = Now.AddMinutes(5);

        DemoDataHostedService.DeferIfDue(Now.AddMinutes(-1), Now, retryAt).Should().Be(retryAt);
        DemoDataHostedService.DeferIfDue(Now, Now, retryAt).Should().Be(retryAt);
        DemoDataHostedService.DeferIfDue(Now.AddMinutes(2), Now, retryAt).Should().Be(Now.AddMinutes(2));
        DemoDataHostedService.DeferIfDue((DateTime?)null, Now, retryAt).Should().BeNull();
    }

    [Fact]
    public async Task A_stopping_token_that_is_cancelled_ends_the_loop_without_an_error()
    {
        var api = new TimeoutApi(Now) { BlockOn = (call, nth) => call == Entry && nth == 2 };

        var run = await RunAsync(api, new DemoModeConfiguration());
        await run.AdvanceUntilAsync("the tick that blocks", () => api.CountOf(Entry) >= 2);

        await run.StopAsync();

        run.Service.State.Should().Be(DemoServiceState.Stopped);
        run.Logger.Messages.Should().Contain("Demo data generation service is stopping");
        run.Logger.Errors.Should().BeEmpty();
    }

    private static async Task<Run> RunAsync(TimeoutApi api, DemoModeConfiguration config)
    {
        config.VisitorResetIntervalMinutes = 0;
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        api.Clock = clock;
        var logger = new ListLogger<DemoDataHostedService>();
        var options = Options.Create(config);
        var service = new DemoDataHostedService(
            new DemoApiClient(new FakeHttpClientFactory(api), NullLogger<DemoApiClient>.Instance),
            options,
            new DemoDataGenerator(
                options,
                new DemoClock(clock, TimeZoneInfo.Utc),
                NullLogger<DemoDataGenerator>.Instance,
                NullLoggerFactory.Instance),
            new DemoServiceHealthCheck(NullLogger<DemoServiceHealthCheck>.Instance),
            clock,
            logger);

        await service.StartAsync(CancellationToken.None);
        var run = new Run(service, clock, logger);
        await Eventually.UntilAsync("the first realtime tick", () => api.CountOf(Entry) >= 1);
        return run;
    }

    private sealed class Run(DemoDataHostedService service, FakeTimeProvider clock, ListLogger<DemoDataHostedService> logger)
        : IAsyncDisposable
    {
        private bool _stopped;

        public DemoDataHostedService Service => service;

        public ListLogger<DemoDataHostedService> Logger => logger;

        public void Advance(TimeSpan by) => clock.Advance(by);

        public Task AdvanceUntilAsync(string description, Func<bool> condition) =>
            Eventually.UntilAsync(
                description,
                condition,
                step: TimeSpan.FromMilliseconds(5),
                eachStep: () => clock.Advance(TimeSpan.FromSeconds(30)));

        public async Task StopAsync()
        {
            _stopped = true;
            await service.StopAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stopped)
                await StopAsync();
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://demo.test/") };
    }

    /// <summary>
    /// The demo admin and tenant endpoints over a tenant holding fresh data, so a start keeps it. A call can be made to
    /// time out as <c>HttpClient</c> does, with a <see cref="TaskCanceledException"/> its caller's token did not cause.
    /// </summary>
    private sealed class TimeoutApi(DateTime now) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly Dictionary<string, int> _counts = [];

        public FakeTimeProvider Clock { get; set; } = null!;

        public DateTime LastResetAt { get; set; } = now.AddDays(-1);

        public Func<string, int, bool> TimeOutOn { get; set; } = (_, _) => false;

        public Func<string, int, bool> BlockOn { get; set; } = (_, _) => false;

        /// <summary>How long a reset takes on the clock, so the next reading is due when it returns.</summary>
        public TimeSpan ResetTakes { get; set; }

        public Func<string, int, bool> RefuseOn { get; set; } = (_, _) => false;

        private readonly Dictionary<string, List<DateTime>> _calledAt = [];

        public List<DateTime> CalledAt(string call)
        {
            lock (_counts)
                return [.. _calledAt.GetValueOrDefault(call) ?? []];
        }

        public int CountOf(string call)
        {
            lock (_counts)
                return _counts.GetValueOrDefault(call);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            if (call == "POST /api/v4/admin/demo/seed-extras"
                && (await request.Content!.ReadAsStringAsync(cancellationToken)).Contains("\"person\"", StringComparison.Ordinal))
                call = PersonSeed;
            int nth;
            lock (_counts)
            {
                _counts[call] = nth = _counts.GetValueOrDefault(call) + 1;
                if (!_calledAt.TryGetValue(call, out var times))
                    _calledAt[call] = times = [];
                times.Add(Clock.GetUtcNow().UtcDateTime);
            }

            if (TimeOutOn(call, nth))
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
            if (RefuseOn(call, nth))
                throw new HttpRequestException("Connection refused");
            if (BlockOn(call, nth))
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return call switch
            {
                "POST /api/v4/admin/demo/provision" or "GET /api/v4/admin/demo/status" =>
                    Ok(new { tenantId = "demo", isActive = true, lastResetAt = LastResetAt }),
                "GET /api/v4/glucose/sensor" => Ok(new
                {
                    data = new[]
                    {
                        new
                        {
                            mgdl = 120.0,
                            timestamp = request.RequestUri.Query.Contains("timestamp_asc")
                                ? now.AddDays(-91)
                                : now.AddMinutes(-2),
                        },
                    },
                }),
                Reset => ResetOk(),
                _ => Ok(new { }),
            };

            HttpResponseMessage ResetOk()
            {
                LastResetAt = Clock.GetUtcNow().UtcDateTime;
                Clock.Advance(ResetTakes);
                return Ok(new { tenantId = "demo" });
            }
        }

        private static HttpResponseMessage Ok(object body) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Json) };
    }
}
