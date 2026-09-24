using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Services;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Services;

public class CareLinkAuthFlowServiceTests
{
    [Theory]
    [InlineData("EU", CareLinkConstants.Discovery.EuBaseUrl)]
    [InlineData("US", CareLinkConstants.Discovery.UsBaseUrl)]
    [InlineData("eu", CareLinkConstants.Discovery.EuBaseUrl)]
    public void GetDiscoveryUrl_ReturnsCorrectUrl(string server, string expectedBase)
    {
        var url = CareLinkAuthFlowService.GetDiscoveryUrl(server);
        url.Should().Be($"{expectedBase}{CareLinkConstants.Discovery.DiscoveryPath}");
    }

    [Fact]
    public void GeneratePkce_ProducesValidCodeVerifierAndChallenge()
    {
        var (verifier, challenge) = CareLinkAuthFlowService.GeneratePkce();
        verifier.Should().NotBeNullOrEmpty();
        challenge.Should().NotBeNullOrEmpty();
        verifier.Should().NotBe(challenge);
        verifier.Should().NotContainAny("+", "/", "=");
        challenge.Should().NotContainAny("+", "/", "=");
    }

    /// <summary>
    /// CareLink's Auth0 tenant sits behind CloudFront, whose WAF answers any request carrying no
    /// User-Agent with a 403 "Request blocked" HTML page instead of reaching Auth0 at all. Since
    /// HttpClient sends no User-Agent by default, every request this service makes must carry one.
    /// </summary>
    [Fact]
    public async Task ExchangeCodeAsync_SendsAUserAgent()
    {
        var handler = new CareLinkFakeHandler();
        using var flow = new CareLinkAuthFlowService(NullLogger.Instance, handler);

        var result = await flow.ExchangeCodeAsync(
            "auth-code", "code-verifier", CareLinkFakeHandler.ClientId, CareLinkFakeHandler.TokenUrl,
            "com.medtronic.carepartner:/sso", CareLinkFakeHandler.Audience, CancellationToken.None);

        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("new-access-token");
        handler.Requests.Should().NotBeEmpty();
        handler.Requests.Should().OnlyContain(r => r.UserAgent != null,
            "CloudFront blocks requests with no User-Agent before they reach Auth0");
    }

    [Fact]
    public async Task ResolveSsoParametersAsync_ReturnsClientIdAndTokenUrlFromDiscovery()
    {
        var handler = new CareLinkFakeHandler();
        using var flow = new CareLinkAuthFlowService(NullLogger.Instance, handler);

        var sso = await flow.ResolveSsoParametersAsync("EU", CancellationToken.None);

        sso.Should().NotBeNull();
        sso!.ClientId.Should().Be(CareLinkFakeHandler.ClientId);
        sso.TokenUrl.Should().Be(CareLinkFakeHandler.TokenUrl);
        sso.Audience.Should().Be(CareLinkFakeHandler.Audience);
        handler.Requests.Should().OnlyContain(r => r.UserAgent != null);
    }

    /// <summary>
    /// The fake leaves the authorize endpoint unmodelled, so it answers 404 and never serves a login
    /// form. That is the WAF-block or changed-markup case, which a repeat cannot clear.
    /// </summary>
    [Fact]
    public async Task LoginAsync_WhenTheAuthorizeEndpointNeverYieldsAForm_ReturnsNonRetryableVerdict()
    {
        var handler = new CareLinkFakeHandler();
        using var flow = new CareLinkAuthFlowService(NullLogger.Instance, handler);

        var (result, shouldRetry) = await flow.LoginAsync(
            "user@example.com", "hunter2", "EU", CancellationToken.None);

        result.Should().BeNull();
        shouldRetry.Should().BeFalse();
    }

    [Fact]
    public async Task LoginAsync_WhenTheLoginFormHasNoAction_ReturnsNonRetryableVerdict()
    {
        var handler = new CareLinkFakeHandler
        {
            AuthorizeBody = "<html><form method=\"post\"><input type=\"hidden\" name=\"state\" value=\"s\" /></form></html>",
        };
        using var flow = new CareLinkAuthFlowService(NullLogger.Instance, handler);

        var (result, shouldRetry) = await flow.LoginAsync(
            "user@example.com", "hunter2", "EU", CancellationToken.None);

        result.Should().BeNull();
        shouldRetry.Should().BeFalse();
    }
}
