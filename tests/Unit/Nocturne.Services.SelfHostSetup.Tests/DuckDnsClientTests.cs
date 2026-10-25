using System.Net;
using FluentAssertions;
using Nocturne.Services.SelfHostSetup;
using Xunit;

namespace Nocturne.Services.SelfHostSetup.Tests;

public class DuckDnsClientTests
{
    private const string Token = "01234567-89ab-cdef-0123-456789abcdef";

    [Fact]
    public async Task TheRecordIsClearedThenPointedAtThePublicAddress()
    {
        var handler = new StubDuckDns("OK");

        var outcome = await new DuckDnsClient(new HttpClient(handler))
            .PointAtAsync("myname", Token, IPAddress.Parse("203.0.113.7"), CancellationToken.None);

        outcome.Should().Be(DuckDnsOutcome.Ok);
        handler.Requests.Should().Equal(
            $"https://www.duckdns.org/update?domains=myname&token={Token}&clear=true",
            $"https://www.duckdns.org/update?domains=myname&token={Token}&ip=203.0.113.7");
    }

    [Fact]
    public async Task KoIsARefusalAndStopsBeforeSettingTheAddress()
    {
        var handler = new StubDuckDns("KO");

        var outcome = await new DuckDnsClient(new HttpClient(handler))
            .PointAtAsync("myname", Token, IPAddress.Parse("203.0.113.7"), CancellationToken.None);

        outcome.Should().Be(DuckDnsOutcome.Refused);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ANetworkFailureIsUnreachable()
    {
        var outcome = await new DuckDnsClient(new HttpClient(new StubDuckDns(null)))
            .PointAtAsync("myname", Token, IPAddress.Parse("203.0.113.7"), CancellationToken.None);

        outcome.Should().Be(DuckDnsOutcome.Unreachable);
    }

    private sealed class StubDuckDns(string? body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (body is null)
                throw new HttpRequestException("unreachable");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
