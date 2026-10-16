using FluentAssertions;
using Nocturne.Aspire.Host.Publishing;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace Nocturne.Aspire.Host.Tests.Publishing;

public class ScopeWatchtowerToStackTests
{
    private const string EnableLabel = "com.centurylinklabs.watchtower.enable";

    private const string Compose = """
        services:
          other:
            image: other:1
            configs:
            - source: other-config
              target: /etc/other.conf
          caddy:
            image: caddy:2
          nocturne-api:
            image: nocturne-api
            labels:
              existing: "kept"
          watchtower:
            image: watchtower
        configs:
          other-config:
            content: "x"
        """;

    private const string RawCompose = """
        services:
          nocturne-postgres:
            image: postgres:17.6
            volumes:
            - type: bind
              source: ./init
              target: /docker-entrypoint-initdb.d
              read_only: true
          nocturne-api:
            image: nocturne-api
            depends_on:
              nocturne-postgres:
                condition: service_started
          caddy:
            image: caddy:2
            volumes:
            - type: bind
              source: ./caddy/Caddyfile
              target: /etc/caddy/Caddyfile
              read_only: true
          watchtower:
            image: watchtower
        """;

    private static string? Label(string compose, string service)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(compose));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var node = (YamlMappingNode)((YamlMappingNode)root["services"])[service];
        var labels = (YamlMappingNode)node["labels"];
        return ((YamlScalarNode)labels[EnableLabel]).Value;
    }

    [Fact]
    public void ServiceWithConfigsIsExcludedWhateverItIsNamed()
    {
        var result = PortainerComposePublisherExtensions.ScopeWatchtowerToStack(Compose);

        Label(result, "other").Should().Be("false");
    }

    [Fact]
    public void ServiceWithoutConfigsIsIncludedWhateverItIsNamed()
    {
        var result = PortainerComposePublisherExtensions.ScopeWatchtowerToStack(Compose);

        Label(result, "caddy").Should().Be("true");
    }

    [Fact]
    public void ServiceWithoutConfigsKeepsItsLabels()
    {
        var result = PortainerComposePublisherExtensions.ScopeWatchtowerToStack(Compose);

        Label(result, "nocturne-api").Should().Be("true");
        result.Should().Contain("existing: \"kept\"");
    }

    [Fact]
    public void WatchtowerStillUpdatesItself()
    {
        var result = PortainerComposePublisherExtensions.ScopeWatchtowerToStack(Compose);

        Label(result, "watchtower").Should().Be("true");
    }

    [Fact]
    public void SelfContainedComposeExcludesInlinedServicesOnly()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var init = Path.Combine(dir.FullName, "00-init.sh");
            var caddyfile = Path.Combine(dir.FullName, "Caddyfile");
            File.WriteAllText(init, "#!/bin/bash\n");
            File.WriteAllText(caddyfile, "{$BASE_DOMAIN} {\n}\n");

            var result = PortainerComposePublisherExtensions.SelfContainCompose(RawCompose, init, caddyfile);

            Label(result, "nocturne-postgres").Should().Be("false");
            Label(result, "caddy").Should().Be("false");
            Label(result, "nocturne-api").Should().Be("true");
            Label(result, "watchtower").Should().Be("true");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
