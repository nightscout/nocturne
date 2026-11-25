using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Compatibility;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Services.CompatibilityProxy.Tests.Unit;

public class ForwardingResilienceTests
{
    private const string UpstreamUrl = "https://ns.example/api/v1/treatments";

    private sealed class ScriptedHandler(FakeTimeProvider time, TimeSpan answerAfter) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (answerAfter > TimeSpan.Zero)
            {
                await Task.Delay(answerAfter, time, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private static ServiceProvider Provider(
        string clientName,
        ScriptedHandler handler,
        FakeTimeProvider time,
        Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddCompatibilityProxyServices(
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build());
        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        ServiceProvider provider, string clientName, HttpMethod method, FakeTimeProvider time)
    {
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        using var request = new HttpRequestMessage(method, UpstreamUrl);
        if (method != HttpMethod.Get)
            request.Content = new StringContent("{}");

        var work = client.SendAsync(request);
        await Eventually.UntilAsync(
            "the forwarded request to complete",
            () => work.IsCompleted,
            step: TimeSpan.FromMilliseconds(1),
            eachStep: () => time.Advance(TimeSpan.FromSeconds(1)));

        return await work;
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task A_forwarded_write_that_is_answered_with_a_server_error_reaches_the_upstream_once(
        string method)
    {
        var time = new FakeTimeProvider();
        var handler = new ScriptedHandler(time, TimeSpan.Zero);
        await using var provider = Provider("NightscoutClient", handler, time);

        using var response = await SendAsync(provider, "NightscoutClient", new HttpMethod(method), time);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_forwarded_read_that_is_answered_with_a_server_error_is_retried()
    {
        var time = new FakeTimeProvider();
        var handler = new ScriptedHandler(time, TimeSpan.Zero);
        await using var provider = Provider("NightscoutClient", handler, time);

        using var response = await SendAsync(provider, "NightscoutClient", HttpMethod.Get, time);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(handler.Requests > 1);
    }

    [Fact]
    public async Task An_attempt_may_run_as_long_as_the_configured_timeout()
    {
        var time = new FakeTimeProvider();
        var handler = new ScriptedHandler(time, TimeSpan.FromSeconds(12));
        await using var provider = Provider(
            "NightscoutClient",
            handler,
            time,
            new() { ["Parameters:CompatibilityProxy:TimeoutSeconds"] = "15" });

        using var response = await SendAsync(provider, "NightscoutClient", HttpMethod.Post, time);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_discrepancy_report_that_is_answered_with_a_server_error_is_sent_once()
    {
        var time = new FakeTimeProvider();
        var handler = new ScriptedHandler(time, TimeSpan.Zero);
        await using var provider = Provider(DiscrepancyForwardingService.HttpClientName, handler, time);

        using var response = await SendAsync(
            provider, DiscrepancyForwardingService.HttpClientName, HttpMethod.Post, time);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_discrepancy_attempt_may_run_as_long_as_its_own_configured_timeout()
    {
        var time = new FakeTimeProvider();
        var handler = new ScriptedHandler(time, TimeSpan.FromSeconds(12));
        await using var provider = Provider(
            DiscrepancyForwardingService.HttpClientName,
            handler,
            time,
            new()
            {
                ["Parameters:CompatibilityProxy:DiscrepancyForwarding:TimeoutSeconds"] = "15",
            });

        using var response = await SendAsync(
            provider, DiscrepancyForwardingService.HttpClientName, HttpMethod.Post, time);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Requests);
    }
}
