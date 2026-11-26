using System.Diagnostics;
using System.Net.WebSockets;
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
using Nocturne.API.Tests.TestDoubles;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Nightscout;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Infrastructure.Outbound;
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
    public async Task The_realtime_listener_upgrades_to_a_websocket_whose_span_holds_the_masked_url()
    {
        var site = new EngineIoSite();
        await using var app = await site.StartAsync();
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (spans, port) = await ObserveAsync(
            app,
            async (services, port, upgraded) =>
            {
                using var client = NightscoutConnectorBackgroundService.NewClient(
                    NightscoutAddress.Resolve($"http://127.0.0.1:{port}/{Secret}/?token={Token}"),
                    services.GetRequiredService<IHttpClientFactory>(),
                    services.GetRequiredService<IHttpMessageHandlerFactory>());
                client.On("dataUpdate", _ =>
                {
                    updated.TrySetResult();
                    return Task.CompletedTask;
                });
                using var deadline = new CancellationTokenSource(Eventually.DefaultPatience);

                await client.ConnectAsync(deadline.Token);
                await upgraded.WaitAsync(deadline.Token);
                await updated.Task.WaitAsync(deadline.Token);
            },
            spans => spans.Any(IsUpgrade));

        site.WebSocketUserAgent.Should().StartWith("Nocturne").And.Be(site.PollingUserAgent);
        spans.Should().Contain(span => IsUpgrade(span));
        spans.Should().AllSatisfy(span =>
        {
            span.GetTagItem("url.full").Should().Be($"127.0.0.1:{port}/AGYL…/socket.io");
            HoldsNoCredential(span);
        });
    }

    [Fact]
    public async Task The_realtime_listeners_websocket_is_refused_a_link_local_target()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Connectors:Nightscout:Enabled"] = "true";
        new NightscoutConnectorInstaller().Install(builder.Services, builder.Configuration);
        using var host = builder.Build();
        using var http = host.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(NightscoutConnectorInstaller.RealtimeHttpClientName);
        using var socket = new ConnectorClientWebSocket(
            host.Services.GetRequiredService<IHttpMessageHandlerFactory>(),
            NightscoutConnectorInstaller.RealtimeHttpClientName,
            http.DefaultRequestHeaders);

        var connect = () => socket.ConnectAsync(
            new Uri($"ws://169.254.169.254/{Secret}/socket.io/?EIO=4&transport=websocket"),
            CancellationToken.None);

        await connect.Should().ThrowAsync<WebSocketException>()
            .WithInnerException(typeof(OutboundRefusedException));
    }

    private static bool IsUpgrade(Activity span) =>
        span.GetTagItem("http.response.status_code") is 101;

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
