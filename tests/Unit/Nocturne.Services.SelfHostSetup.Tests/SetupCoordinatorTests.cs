using System.Net;
using FluentAssertions;
using Nocturne.Services.SelfHostSetup;
using Xunit;

namespace Nocturne.Services.SelfHostSetup.Tests;

public sealed class SetupCoordinatorTests : IDisposable
{
    private const string Claim = "i-0123456789abcdef0";
    private const string Token = "01234567-89ab-cdef-0123-456789abcdef";
    private static readonly IPAddress PublicIp = IPAddress.Parse("203.0.113.7");

    private readonly string _configDirectory = Directory.CreateTempSubdirectory("selfhost-setup-").FullName;
    private readonly StubDuckDns _duckDns = new("OK");
    private readonly StubResolver _resolver = new();

    public SetupCoordinatorTests()
    {
        File.WriteAllText(EnvPath, "INSTANCE_KEY=secret\nBASE_DOMAIN=\n");
    }

    private string EnvPath => Path.Combine(_configDirectory, ".env");

    private SetupCoordinator Coordinator(ClaimGuard? guard = null) => new(
        guard ?? new ClaimGuard(Claim),
        new DuckDnsClient(new HttpClient(_duckDns)),
        new DnsProbe(_resolver),
        new SetupOptions(_configDirectory, PublicIp));

    [Fact]
    public async Task DuckDnsSetsTheBaseDomainAndTurnsOnTheUpdater()
    {
        var outcome = await Coordinator().UseDuckDnsAsync(Claim, "myname", Token, CancellationToken.None);

        outcome.Should().Be(new SetupOutcome.Completed("myname.duckdns.org"));
        File.ReadAllText(EnvPath).Should().Be(
            "INSTANCE_KEY=secret\nBASE_DOMAIN=myname.duckdns.org\n"
            + "COMPOSE_PROFILES=duckdns\nDUCKDNS_SUBDOMAIN=myname\nDUCKDNS_TOKEN=" + Token + "\n");
        Coordinator().IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task AWrongClaimCodeTouchesNothing()
    {
        var outcome = await Coordinator().UseDuckDnsAsync("wrong", "myname", Token, CancellationToken.None);

        outcome.Should().Be(new SetupOutcome.ClaimRejected());
        _duckDns.Requests.Should().BeEmpty();
        File.ReadAllText(EnvPath).Should().Be("INSTANCE_KEY=secret\nBASE_DOMAIN=\n");
    }

    [Fact]
    public async Task ALockedGuardIsReported()
    {
        var guard = new ClaimGuard(Claim);
        for (var i = 0; i < 5; i++)
            guard.Check("wrong");

        var outcome = await Coordinator(guard).UseDuckDnsAsync(Claim, "myname", Token, CancellationToken.None);

        outcome.Should().Be(new SetupOutcome.LockedOut());
    }

    [Fact]
    public async Task AMalformedDuckDnsTokenIsInvalidInput()
    {
        var outcome = await Coordinator().UseDuckDnsAsync(Claim, "myname", "nope", CancellationToken.None);

        outcome.Should().BeOfType<SetupOutcome.Invalid>();
        _duckDns.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ADuckDnsRefusalWritesNothing()
    {
        _duckDns.Body = "KO";

        var outcome = await Coordinator().UseDuckDnsAsync(Claim, "myname", Token, CancellationToken.None);

        outcome.Should().Be(new SetupOutcome.DuckDnsFailed(DuckDnsOutcome.Refused));
        Coordinator().IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task ACustomDomainCompletesOnceTheApexAndWildcardPointHere()
    {
        _resolver.Answers["nocturne.example.com"] = [PublicIp];
        _resolver.Answers[$"{DnsProbe.WildcardProbeLabel}.nocturne.example.com"] = [PublicIp];

        var outcome = await Coordinator().UseCustomDomainAsync(Claim, "nocturne.example.com", CancellationToken.None);

        outcome.Should().Be(new SetupOutcome.Completed("nocturne.example.com"));
        File.ReadAllText(EnvPath).Should().Be(
            "INSTANCE_KEY=secret\nBASE_DOMAIN=nocturne.example.com\nCOMPOSE_PROFILES=\n");
    }

    [Fact]
    public async Task ACustomDomainWaitsForEveryRecordItNeeds()
    {
        _resolver.Answers["nocturne.example.com"] = [PublicIp];
        _resolver.Answers[$"{DnsProbe.WildcardProbeLabel}.nocturne.example.com"] = [IPAddress.Parse("198.51.100.1")];

        var outcome = await Coordinator().UseCustomDomainAsync(Claim, "nocturne.example.com", CancellationToken.None);

        outcome.Should().Be(new SetupOutcome.DnsPending("nocturne.example.com", PublicIp, ApexReady: true, WildcardReady: false));
        Coordinator().IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task SetupRunsOnce()
    {
        await Coordinator().UseDuckDnsAsync(Claim, "myname", Token, CancellationToken.None);

        var outcome = await Coordinator().UseDuckDnsAsync(Claim, "other", Token, CancellationToken.None);

        outcome.Should().Be(new SetupOutcome.AlreadyComplete());
        File.ReadAllText(EnvPath).Should().Contain("BASE_DOMAIN=myname.duckdns.org");
    }

    public void Dispose() => Directory.Delete(_configDirectory, recursive: true);

    private sealed class StubDuckDns(string body) : HttpMessageHandler
    {
        public string Body { get; set; } = body;
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) });
        }
    }

    private sealed class StubResolver : IDnsResolver
    {
        public Dictionary<string, IPAddress[]> Answers { get; } = [];

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(Answers.GetValueOrDefault(host, []));
    }
}
