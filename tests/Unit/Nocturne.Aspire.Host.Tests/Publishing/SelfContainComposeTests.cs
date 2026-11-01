using FluentAssertions;
using Nocturne.Aspire.Host.Publishing;
using YamlDotNet.RepresentationModel;
using Xunit;
using static Nocturne.Aspire.Host.Tests.Publishing.ComposeYaml;

namespace Nocturne.Aspire.Host.Tests.Publishing;

public class SelfContainComposeTests : ComposeTransformTestBase
{
    private const string Compose = """
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
            environment:
              WATCHTOWER_HTTP_API_ENDPOINTS: "history,config"
              WATCHTOWER_HTTP_API_TOKEN: "${WATCHTOWER_API_TOKEN}"
        """;

    private string Run()
        => PortainerComposePublisherExtensions.SelfContainCompose(
            Compose, WriteFile("00-init.sh", "#!/bin/bash\n"), WriteFile("Caddyfile", "{$BASE_DOMAIN} {\n}\n"));

    [Fact]
    public void EveryPassLeavesItsMark()
    {
        var result = Run();

        Text(ConfigReference(Service(result, Postgres))["source"]).Should().Be("nocturne-init");
        Text(ConfigReference(Service(result, "caddy"))["source"]).Should().Be("nocturne-caddyfile");
        Configs(result).Children.Keys.Select(Text).Should().BeEquivalentTo("nocturne-init", "nocturne-caddyfile");
        Service(result, Postgres).Children.Keys.Select(Text).Should().Contain("healthcheck");
        Text(((YamlMappingNode)((YamlMappingNode)Service(result, "nocturne-api")["depends_on"])[Postgres])["condition"])
            .Should().Be("service_healthy");
        EnvValue(result, "watchtower", "WATCHTOWER_HTTP_API_ENDPOINTS")
            .Should().Be("${WATCHTOWER_API_TOKEN:+history,config}");
    }

    [Fact]
    public void WatchtowerIsScopedAfterTheConfigsAreInlined()
    {
        var result = Run();

        Label(result, Postgres).Should().Be("false");
        Label(result, "caddy").Should().Be("false");
        Label(result, "nocturne-api").Should().Be("true");
        Label(result, "watchtower").Should().Be("true");
    }

    [Fact]
    public void ApplyingItToItsOwnOutputChangesNothingMore()
    {
        var once = Run();

        PortainerComposePublisherExtensions.SelfContainCompose(
            once, "missing-init.sh", "missing-Caddyfile").Should().Be(once);
    }
}
