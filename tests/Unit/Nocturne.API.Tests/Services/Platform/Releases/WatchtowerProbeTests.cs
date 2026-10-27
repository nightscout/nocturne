using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Platform.Releases;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform.Releases;

public class WatchtowerProbeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnUnresolvableHostIsNotDetected()
    {
        var probe = CreateProbe(_ => throw new InvalidOperationException("must not call"),
            url: "http://watchtower.invalid:8080", token: "t");

        var status = await probe.ProbeAsync(CancellationToken.None);

        status.Mode.Should().Be(AutoUpdateMode.NotDetected);
    }

    [Fact]
    public async Task WithoutATokenItIsOnlyDetected()
    {
        var probe = CreateProbe(_ => throw new InvalidOperationException("must not call"), token: null);

        var status = await probe.ProbeAsync(CancellationToken.None);

        status.Mode.Should().Be(AutoUpdateMode.Detected);
        status.CheckIntervalSeconds.Should().Be(86400);
        status.NextCheckAt.Should().BeNull();
    }

    [Fact]
    public async Task ARefusedTokenIsOnlyDetected()
    {
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var status = await probe.ProbeAsync(CancellationToken.None);

        status.Mode.Should().Be(AutoUpdateMode.Detected);
    }

    [Fact]
    public async Task AnUnreachableApiIsOnlyDetected()
    {
        var probe = CreateProbe(_ => throw new HttpRequestException("connection refused"));

        var status = await probe.ProbeAsync(CancellationToken.None);

        status.Mode.Should().Be(AutoUpdateMode.Detected);
    }

    [Fact]
    public async Task TheNextCheckIsTheLastScanPlusTheInterval()
    {
        var lastScan = Now.AddHours(-3);
        string? authorization = null;
        var probe = CreateProbe(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/config" => Json("""{"config":{"monitor_only":false,"cleanup":true}}"""),
                "/v1/history" => Json($$"""{"entries":[{"timestamp":"{{lastScan:O}}","scanned":7}],"count":1}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        var status = await probe.ProbeAsync(CancellationToken.None);

        authorization.Should().Be("Bearer secret");
        status.Mode.Should().Be(AutoUpdateMode.Automatic);
        status.LastCheckAt.Should().Be(lastScan);
        status.NextCheckAt.Should().Be(lastScan.AddDays(1));
    }

    [Fact]
    public async Task MonitorOnlyInstallsNothing()
    {
        var probe = CreateProbe(request => request.RequestUri!.AbsolutePath == "/v1/config"
            ? Json("""{"config":{"monitor_only":true}}""")
            : new HttpResponseMessage(HttpStatusCode.NoContent));

        var status = await probe.ProbeAsync(CancellationToken.None);

        status.Mode.Should().Be(AutoUpdateMode.MonitorOnly);
        status.LastCheckAt.Should().BeNull();
        status.NextCheckAt.Should().BeNull();
    }

    [Fact]
    public void ANextCheckAlreadyPassedIsUnknown()
    {
        WatchtowerProbe.NextCheck(Now.AddDays(-2), 86400, Now).Should().BeNull();
        WatchtowerProbe.NextCheck(null, 86400, Now).Should().BeNull();
        WatchtowerProbe.NextCheck(Now.AddHours(-1), null, Now).Should().BeNull();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static WatchtowerProbe CreateProbe(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string url = "http://127.0.0.1:8080",
        string? token = "secret")
    {
        var options = new ReleaseCheckOptions
        {
            Watchtower = new WatchtowerOptions { Url = url, Token = token, PollIntervalSeconds = 86400 },
        };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StubHandler(respond), disposeHandler: true));

        return new WatchtowerProbe(
            factory.Object,
            Options.Create(options),
            new FakeTimeProvider(Now),
            NullLogger<WatchtowerProbe>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
