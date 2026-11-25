using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Services.Demo.Tests.Services;

public class DemoDataHostedServiceStartupTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DemoModeConfiguration Config(
        int resetIntervalMinutes = 0,
        bool clearOnStartup = false,
        bool regenerateOnStartup = false) => new()
    {
        IntervalMinutes = 5,
        ResetIntervalMinutes = resetIntervalMinutes,
        ClearOnStartup = clearOnStartup,
        RegenerateOnStartup = regenerateOnStartup,
    };

    private static GlucoseHistory History(DateTime oldest, DateTime newest) =>
        new(new GlucoseReading(110, oldest), new GlucoseReading(120, newest));

    private static GlucoseHistory SeededHistory(DateTime lastReset, DateTime newest) =>
        History(lastReset.AddDays(-90), newest);

    [Fact]
    public void The_defaults_do_not_force_regeneration()
    {
        var config = new DemoModeConfiguration();

        config.ClearOnStartup.Should().BeFalse();
        config.RegenerateOnStartup.Should().BeFalse();
    }

    [Fact]
    public void Regenerates_an_empty_tenant()
    {
        DemoDataHostedService
            .ShouldRegenerateOnStartup(Config(), Now, Now.AddHours(-1), GlucoseHistory.Empty)
            .Should().BeTrue();
    }

    [Fact]
    public void Keeps_fresh_data_across_a_restart()
    {
        DemoDataHostedService
            .ShouldRegenerateOnStartup(Config(), Now, Now.AddDays(-1), SeededHistory(Now.AddDays(-1), Now.AddMinutes(-7)))
            .Should().BeFalse();
    }

    [Fact]
    public void Keeps_data_whose_newest_reading_is_exactly_at_the_staleness_limit()
    {
        DemoDataHostedService
            .ShouldRegenerateOnStartup(Config(), Now, Now.AddDays(-1), SeededHistory(Now.AddDays(-1), Now.AddMinutes(-15)))
            .Should().BeFalse();
    }

    [Fact]
    public void Regenerates_data_left_stale_by_a_long_outage()
    {
        DemoDataHostedService
            .ShouldRegenerateOnStartup(Config(), Now, Now.AddDays(-1), SeededHistory(Now.AddDays(-1), Now.AddMinutes(-16)))
            .Should().BeTrue();
    }

    [Fact]
    public void Keeps_the_data_when_the_history_could_not_be_read()
    {
        DemoDataHostedService
            .ShouldRegenerateOnStartup(Config(), Now, Now.AddDays(-1), history: null)
            .Should().BeFalse();
    }

    [Fact]
    public void Regenerates_a_history_that_does_not_reach_back_past_the_last_reset()
    {
        var lastReset = Now.AddHours(-2);

        DemoDataHostedService
            .ShouldRegenerateOnStartup(Config(), Now, lastReset, History(lastReset.AddMinutes(1), Now.AddMinutes(-2)))
            .Should().BeTrue();
    }

    [Fact]
    public void Keeps_a_history_with_no_recorded_reset_to_judge_it_by()
    {
        DemoDataHostedService
            .ShouldRegenerateOnStartup(Config(), Now, lastResetUtc: null, History(Now.AddHours(-1), Now.AddMinutes(-2)))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Either_force_setting_regenerates_fresh_data(bool clearOnStartup, bool regenerateOnStartup)
    {
        DemoDataHostedService
            .ShouldRegenerateOnStartup(
                Config(clearOnStartup: clearOnStartup, regenerateOnStartup: regenerateOnStartup),
                Now,
                Now.AddMinutes(-1),
                SeededHistory(Now.AddMinutes(-1), Now.AddMinutes(-1)))
            .Should().BeTrue();
    }

    [Fact]
    public void A_restart_before_the_reset_is_due_keeps_the_data_and_the_original_reset_time()
    {
        var config = Config(resetIntervalMinutes: 60);
        var lastReset = Now.AddMinutes(-40);

        DemoDataHostedService
            .ShouldRegenerateOnStartup(config, Now, lastReset, SeededHistory(lastReset, Now.AddMinutes(-2)))
            .Should().BeFalse();
        DemoDataHostedService.NextResetDue(config, lastReset)
            .Should().Be(Now.AddMinutes(20));
    }

    [Fact]
    public void A_restart_after_the_reset_fell_due_regenerates_even_when_the_history_could_not_be_read()
    {
        DemoDataHostedService
            .ShouldRegenerateOnStartup(Config(resetIntervalMinutes: 60), Now, Now.AddMinutes(-70), history: null)
            .Should().BeTrue();
    }

    [Fact]
    public void A_tenant_with_no_recorded_reset_is_due_for_one()
    {
        DemoDataHostedService
            .ShouldRegenerateOnStartup(
                Config(resetIntervalMinutes: 60), Now, lastResetUtc: null, History(Now.AddDays(-90), Now.AddMinutes(-2)))
            .Should().BeTrue();
    }

    [Fact]
    public void No_reset_is_scheduled_when_periodic_resets_are_off()
    {
        DemoDataHostedService.NextResetDue(Config(), Now.AddDays(-30)).Should().BeNull();
        DemoDataHostedService
            .ShouldRegenerateOnStartup(Config(), Now, Now.AddDays(-30), SeededHistory(Now.AddDays(-30), Now.AddMinutes(-2)))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_start_with_fresh_data_neither_resets_nor_seeds()
    {
        var api = new FakeDemoApi { LastResetAt = Now.AddDays(-1) };
        api.Readings(Now.AddDays(-91), Now.AddMinutes(-2));

        await RunStartupAsync(Config(), api);

        api.Calls.Should().NotContain([FakeDemoApi.Reset, FakeDemoApi.Seed]);
    }

    [Fact]
    public async Task A_start_reads_the_reset_schedule_from_the_tenant()
    {
        var api = new FakeDemoApi { LastResetAt = Now.AddMinutes(-40) };
        api.Readings(Now.AddDays(-90), Now.AddMinutes(-2));

        await RunStartupAsync(Config(resetIntervalMinutes: 60), api);

        api.Calls.Should().NotContain(FakeDemoApi.Reset);
    }

    [Fact]
    public async Task A_transient_read_failure_does_not_wipe_fresh_data()
    {
        var api = new FakeDemoApi { LastResetAt = Now.AddDays(-1), FailedReads = 2 };
        api.Readings(Now.AddDays(-91), Now.AddMinutes(-2));

        await RunStartupAsync(Config(), api);

        api.Calls.Should().NotContain([FakeDemoApi.Reset, FakeDemoApi.Seed]);
    }

    [Fact]
    public async Task A_history_that_cannot_be_read_at_all_keeps_the_data()
    {
        var api = new FakeDemoApi { LastResetAt = Now.AddDays(-1), FailedReads = int.MaxValue };

        await RunStartupAsync(Config(), api);

        api.Calls.Should().NotContain([FakeDemoApi.Reset, FakeDemoApi.Seed]);
        api.Calls.Should().Contain(FakeDemoApi.Tick);
    }

    [Fact]
    public async Task A_start_on_an_empty_tenant_regenerates_and_schedules_from_the_stamped_reset()
    {
        var api = new FakeDemoApi();

        await RunStartupAsync(Config(resetIntervalMinutes: 60), api);

        api.Calls.Should().ContainInOrder(FakeDemoApi.Reset, FakeDemoApi.Seed, FakeDemoApi.StatusUpdate);
        api.StatusPatches.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new StatusPatch(api.LastResetAt!.Value.AddMinutes(60), null));
    }

    [Fact]
    public async Task A_failed_seed_is_not_recorded_as_a_reset()
    {
        var api = new FakeDemoApi { SeedFails = true };

        await RunStartupAsync(Config(resetIntervalMinutes: 60), api);

        api.Calls.Should().ContainInOrder(FakeDemoApi.Reset, FakeDemoApi.Seed);
        api.Calls.Should().NotContain(FakeDemoApi.StatusUpdate);
    }

    [Fact]
    public async Task A_restart_after_a_failed_seed_regenerates_rather_than_keeping_the_realtime_readings()
    {
        var api = new FakeDemoApi { SeedFails = true };
        await RunStartupAsync(Config(), api);
        api.Calls.Clear();
        api.SeedFails = false;

        await RunStartupAsync(Config(), api);

        api.Calls.Should().ContainInOrder(FakeDemoApi.Reset, FakeDemoApi.Seed);
    }

    private static async Task RunStartupAsync(DemoModeConfiguration config, FakeDemoApi api)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        api.Clock = clock;
        api.FirstTick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            NullLogger<DemoDataHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.UntilAsync(
                "the service to reach its first realtime tick",
                () => api.FirstTick.Task.IsCompleted,
                step: TimeSpan.FromMilliseconds(5),
                eachStep: () => clock.Advance(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed record StatusPatch(DateTime? NextResetAt, DateTime? LastResetAt);

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://demo.test/") };
    }

    /// <summary>
    /// The demo admin and tenant endpoints, with the API's reset semantics: a reset empties the
    /// tenant and stamps its last reset time, and a seed backfills 90 days up to now.
    /// </summary>
    private sealed class FakeDemoApi : HttpMessageHandler
    {
        public const string Reset = "POST /api/v4/admin/demo/reset";
        public const string Seed = "POST /api/v4/admin/demo/seed-extras";
        public const string StatusUpdate = "PATCH /api/v4/admin/demo/status";
        public const string Tick = "POST /api/v4/glucose/sensor/bulk";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private DateTime? _oldest;
        private DateTime? _newest;

        public FakeTimeProvider Clock { get; set; } = null!;
        public TaskCompletionSource FirstTick { get; set; } = new();
        public DateTime? LastResetAt { get; set; }
        public int FailedReads { get; set; }
        public bool SeedFails { get; set; }
        public List<string> Calls { get; } = [];
        public List<StatusPatch> StatusPatches { get; } = [];

        public void Readings(DateTime oldest, DateTime newest) => (_oldest, _newest) = (oldest, newest);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            lock (Calls)
                Calls.Add(call);
            var now = Clock.GetUtcNow().UtcDateTime;

            switch (call)
            {
                case "POST /api/v4/admin/demo/provision":
                case "GET /api/v4/admin/demo/status":
                    return Ok(new { tenantId = "demo", isActive = true, lastResetAt = LastResetAt });

                case "GET /api/v4/glucose/sensor":
                    if (FailedReads > 0)
                    {
                        FailedReads--;
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }

                    var edge = request.RequestUri.Query.Contains("timestamp_asc") ? _oldest : _newest;
                    return Ok(new { data = edge is { } at ? new object[] { new { mgdl = 120.0, timestamp = at } } : Array.Empty<object>() });

                case Reset:
                    LastResetAt = now;
                    (_oldest, _newest) = (null, null);
                    return Ok(new { tenantId = "demo" });

                case Seed when SeedFails:
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);

                case Seed:
                    (_oldest, _newest) = (now.AddDays(-90), now);
                    return Ok(new { });

                case StatusUpdate:
                    var patch = await request.Content!.ReadFromJsonAsync<StatusPatch>(Json, cancellationToken);
                    StatusPatches.Add(patch!);
                    return Ok(new { });

                case Tick:
                    _oldest ??= now;
                    _newest = now;
                    FirstTick.TrySetResult();
                    return Ok(new { });

                default:
                    return Ok(new { });
            }
        }

        private static HttpResponseMessage Ok(object body) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Json) };
    }
}
