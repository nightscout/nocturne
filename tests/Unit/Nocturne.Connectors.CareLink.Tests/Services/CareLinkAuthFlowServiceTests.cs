using FluentAssertions;
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
}
