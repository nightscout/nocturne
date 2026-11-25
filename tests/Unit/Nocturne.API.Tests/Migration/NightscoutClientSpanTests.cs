using System.Diagnostics;
using System.Text.Json;
using Nocturne.Tests.Shared.Infrastructure;
using Activity = System.Diagnostics.Activity;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nocturne.API.Extensions;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Nightscout;
using Nocturne.Connectors.Nightscout.Services;
using OpenTelemetry.Trace;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A Nightscout site's path can be its credential, so the span the HTTP client instrumentation
/// records for a request through the import's client, any client of the Nightscout connector, the
/// real-time listener, or the compatibility proxy's clients that reach a Nightscout holds the masked
/// URL, as registered for the running API with the service defaults' tracing.
/// </summary>
public class NightscoutClientSpanTests
{
    private const string Secret = "AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI";
    private const string RedirectSecret = "BGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI";
    private const string Token = "synthetic-token";
    private const string ControlClient = "connector-client-without-the-mark";

    [Theory]
    [MemberData(nameof(NightscoutClientRequestLoggingTests.NightscoutClients), MemberType = typeof(NightscoutClientRequestLoggingTests))]
    public async Task The_span_of_a_request_through_the_client_holds_the_masked_url(string client)
    {
        await using var site = await StartSiteAsync(context => context.Response.WriteAsync("{}"));

        var (spans, port) = await ObserveAsync(site, (services, port) => GetAsync(services, client, port, Secret));

        var span = spans.Should().ContainSingle().Subject;
        span.GetTagItem("url.full").Should().Be($"127.0.0.1:{port}/AGYL…/api/v1/status.json");
        HoldsNoCredential(span);
    }

    [Fact]
    public async Task The_span_of_a_request_without_the_mark_holds_its_url()
    {
        await using var site = await StartSiteAsync(context => context.Response.WriteAsync("{}"));

        var (spans, port) = await ObserveAsync(site, (services, port) => GetAsync(services, ControlClient, port, Secret));

        spans.Should().ContainSingle().Which.GetTagItem("url.full")
            .Should().BeOfType<string>().Which.Should().Contain(Secret);
    }

    [Fact]
    public async Task Each_hop_of_a_redirect_the_link_local_guard_follows_holds_the_masked_url()
    {
        await using var site = await StartSiteAsync(context =>
        {
            if (!context.Request.Path.StartsWithSegments($"/{Secret}"))
                return context.Response.WriteAsync("{}");

            context.Response.Redirect($"/{RedirectSecret}/api/v1/status.json");
            return Task.CompletedTask;
        });

        var (spans, port) = await ObserveAsync(
            site,
            (services, port) => GetAsync(services, nameof(NightscoutConnectorService), port, Secret));

        spans.Select(span => span.GetTagItem("url.full")).Should().Equal(
            $"127.0.0.1:{port}/AGYL…/api/v1/status.json",
            $"127.0.0.1:{port}/BGYL…/api/v1/status.json");
        spans.Should().AllSatisfy(HoldsNoCredential);
    }

    [Fact]
    public async Task The_realtime_listener_stays_on_polling_against_a_site_that_offers_the_websocket_upgrade()
    {
        var site = new EngineIoPollingSite("websocket");
        await using var app = await StartSiteAsync(site.AnswerAsync);

        var (spans, port) = await ObserveAsync(
            app,
            async (services, port, polled) =>
            {
                using var client = NightscoutConnectorBackgroundService.NewClient(
                    NightscoutAddress.Resolve($"http://127.0.0.1:{port}/{Secret}/?token={Token}"),
                    services.GetRequiredService<IHttpClientFactory>());
                using var deadline = new CancellationTokenSource(Eventually.DefaultPatience);

                try
                {
                    await client.ConnectAsync(deadline.Token);
                    await polled.WaitAsync(deadline.Token);

                    client.Connected.Should().BeTrue();
                }
                finally
                {
                    site.WebSocketRequests.Should().Be(0);
                }
            },
            spans => spans.Any(span => span.DisplayName == "GET") && spans.Any(span => span.DisplayName == "POST"));

        spans.Should().AllSatisfy(span =>
        {
            span.DisplayName.Should().BeOneOf("GET", "POST");
            span.GetTagItem("url.full").Should().Be($"127.0.0.1:{port}/AGYL…/socket.io");
            HoldsNoCredential(span);
        });
    }

    private static void HoldsNoCredential(Activity span)
    {
        span.DisplayName.Should().NotContain(Secret).And.NotContain(RedirectSecret).And.NotContain(Token);
        span.TagObjects.Should().NotContain(tag =>
            tag.Value is string
            && (((string)tag.Value).Contains(Secret)
                || ((string)tag.Value).Contains(RedirectSecret)
                || ((string)tag.Value).Contains(Token)));
    }

    private static async Task GetAsync(IServiceProvider services, string client, int port, string secret)
    {
        using var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(client);
        using var response = await http.GetAsync($"http://127.0.0.1:{port}/{secret}/api/v1/status.json?token={Token}");
        response.EnsureSuccessStatusCode();
    }

    private static Task<(List<Activity> Spans, int Port)> ObserveAsync(
        WebApplication site,
        Func<IServiceProvider, int, Task> act) =>
        ObserveAsync(site, (services, port, _) => act(services, port), until: _ => true);

    /// <summary>
    /// A request a client sends in the background can stop after <paramref name="act"/>'s own await returns, so
    /// <paramref name="act"/> takes a task that completes once the spans collected so far satisfy <paramref name="until"/>.
    /// </summary>
    private static async Task<(List<Activity> Spans, int Port)> ObserveAsync(
        WebApplication site,
        Func<IServiceProvider, int, Task, Task> act,
        Func<IReadOnlyList<Activity>, bool> until)
    {
        var port = new Uri(site.Urls.Single()).Port;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["Connectors:Nightscout:Enabled"] = "true",
        });
        builder.AddServiceDefaults();
        builder.Services.AddMigrationServices();
        builder.Services.AddCompatibilityProxyServices(builder.Configuration);
        new NightscoutConnectorInstaller().Install(builder.Services, builder.Configuration);
        builder.Services.AddHttpClient(ControlClient).ConfigureConnectorClient(baseUrl: null);
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<TracerProvider>();

        var spans = new List<Activity>();
        var satisfied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("server.port") is int p && p == port)
                    lock (spans)
                    {
                        spans.Add(activity);
                        if (until(spans))
                            satisfied.TrySetResult();
                    }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await act(host.Services, port, satisfied.Task);

        lock (spans)
            return (spans.ToList(), port);
    }

    /// <summary>
    /// An Engine.IO server, polling only, as far as a client's connect: it opens a session that offers
    /// <paramref name="upgrades"/>, takes the namespace connect, answers it on the next poll, and holds
    /// every poll after. It refuses a WebSocket request and counts it.
    /// </summary>
    private sealed class EngineIoPollingSite(params string[] upgrades)
    {
        private int _polls;
        private int _webSocketRequests;

        public int WebSocketRequests => Volatile.Read(ref _webSocketRequests);

        public async Task AnswerAsync(HttpContext context)
        {
            if (context.Request.Query["transport"] == "websocket"
                || context.Request.Headers.Upgrade.Count > 0)
            {
                Interlocked.Increment(ref _webSocketRequests);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            context.Response.ContentType = "text/plain; charset=UTF-8";
            if (HttpMethods.IsPost(context.Request.Method))
            {
                await context.Response.WriteAsync("ok");
                return;
            }

            if (!context.Request.Query.ContainsKey("sid"))
            {
                await context.Response.WriteAsync(
                    $"0{{\"sid\":\"session\",\"upgrades\":{JsonSerializer.Serialize(upgrades)},\"pingInterval\":25000,\"pingTimeout\":20000,\"maxPayload\":1000000}}");
                return;
            }

            if (Interlocked.Increment(ref _polls) == 1)
            {
                await context.Response.WriteAsync("40{\"sid\":\"namespace\"}");
                return;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task<WebApplication> StartSiteAsync(RequestDelegate answer)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var site = builder.Build();
        site.Run(answer);
        await site.StartAsync();
        return site;
    }
}
