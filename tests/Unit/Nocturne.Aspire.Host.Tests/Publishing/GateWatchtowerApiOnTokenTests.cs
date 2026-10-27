using FluentAssertions;
using Nocturne.Aspire.Host.Publishing;
using YamlDotNet.RepresentationModel;
using Xunit;

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

    private static string? Env(string compose, string key)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(compose));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var watchtower = (YamlMappingNode)((YamlMappingNode)root["services"])["watchtower"];
        return ((YamlScalarNode)((YamlMappingNode)watchtower["environment"])[key]).Value;
    }

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

    [Fact]
    public void TheSelfContainedComposeGatesTheEndpoints()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var init = Path.Combine(dir.FullName, "00-init.sh");
            var caddyfile = Path.Combine(dir.FullName, "Caddyfile");
            File.WriteAllText(init, "#!/bin/bash\n");
            File.WriteAllText(caddyfile, "{$BASE_DOMAIN} {\n}\n");

            var result = PortainerComposePublisherExtensions.SelfContainCompose(Compose, init, caddyfile);

            Env(result, "WATCHTOWER_HTTP_API_ENDPOINTS").Should().Be("${WATCHTOWER_API_TOKEN:+history,config}");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
