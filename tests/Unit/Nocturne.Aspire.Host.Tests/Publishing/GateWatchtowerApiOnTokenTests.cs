using FluentAssertions;
using Nocturne.Aspire.Host.Publishing;
using Xunit;
using static Nocturne.Aspire.Host.Tests.Publishing.ComposeYaml;

namespace Nocturne.Aspire.Host.Tests.Publishing;

public class GateWatchtowerApiOnTokenTests
{
    private const string Compose = """
        services:
          watchtower:
            image: watchtower
            environment:
              WATCHTOWER_POLL_INTERVAL: "${WATCHTOWER_POLL_INTERVAL}"
              WATCHTOWER_HTTP_API_ENDPOINTS: "history,config"
              WATCHTOWER_HTTP_API_TOKEN: "${WATCHTOWER_API_TOKEN}"
        """;

    private static string? Env(string compose, string key) => EnvValue(compose, "watchtower", key);

    [Fact]
    public void TheEndpointsTurnOnOnlyWhenTheTokenIsSet()
    {
        var result = PortainerComposePublisherExtensions.GateWatchtowerApiOnToken(Compose);

        Env(result, "WATCHTOWER_HTTP_API_ENDPOINTS").Should().Be("${WATCHTOWER_API_TOKEN:+history,config}");
        Env(result, "WATCHTOWER_HTTP_API_TOKEN").Should().Be("${WATCHTOWER_API_TOKEN}");
        Env(result, "WATCHTOWER_POLL_INTERVAL").Should().Be("${WATCHTOWER_POLL_INTERVAL}");
    }

    [Fact]
    public void ApplyingItTwiceChangesNothingMore()
    {
        var once = PortainerComposePublisherExtensions.GateWatchtowerApiOnToken(Compose);

        PortainerComposePublisherExtensions.GateWatchtowerApiOnToken(once).Should().Be(once);
    }

    [Fact]
    public void ATokenThatIsNotAVariableLeavesTheComposeUnchanged()
    {
        var compose = Compose.Replace("${WATCHTOWER_API_TOKEN}", "literal-token");

        PortainerComposePublisherExtensions.GateWatchtowerApiOnToken(compose).Should().Be(compose);
    }

    [Fact]
    public void AComposeWithoutWatchtowerIsUnchanged()
    {
        const string compose = """
            services:
              nocturne-api:
                image: nocturne-api
            """;

        PortainerComposePublisherExtensions.GateWatchtowerApiOnToken(compose).Should().Be(compose);
    }
}
