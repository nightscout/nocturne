using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Extensions;
using Nocturne.Connectors.Nightscout.Configurations;
using Xunit;

namespace Nocturne.Services.CompatibilityProxy.Tests.Unit;

public class CompatibilityProxyHealthCheckTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static async Task<(HealthCheckResult Result, RecordingHandler Handler)> CheckAsync(string nightscoutUrl)
    {
        var handler = new RecordingHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient("NightscoutClient"))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var check = new CompatibilityProxyHealthCheck(
            Options.Create(new CompatibilityProxyConfiguration()),
            factory.Object,
            NullLogger<CompatibilityProxyHealthCheck>.Instance,
            new NightscoutConnectorConfiguration { Url = nightscoutUrl });

        var result = await check.CheckHealthAsync(new HealthCheckContext());
        return (result, handler);
    }

    [Theory]
    [InlineData("https://ns.example", "https://ns.example/")]
    [InlineData("https://ns.example/nightscout", "https://ns.example/nightscout/")]
    [InlineData("https://ns.example/nightscout?token=synthetic-token", "https://ns.example/nightscout/")]
    [InlineData("ns.example/nightscout", "https://ns.example/nightscout/")]
    public async Task The_health_probe_heads_the_configured_base(string nightscoutUrl, string expected)
    {
        var (result, handler) = await CheckAsync(nightscoutUrl);

        var probe = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, probe.Method);
        Assert.Equal(expected, probe.RequestUri?.AbsoluteUri);
        Assert.Equal("healthy", result.Data["nightscout"]);
    }

    [Fact]
    public async Task An_address_that_is_not_http_reads_as_unhealthy_without_a_request()
    {
        var (result, handler) = await CheckAsync("ftp://ns.example/nightscout");

        Assert.Empty(handler.Requests);
        Assert.Equal("unhealthy", result.Data["nightscout"]);
    }
}
