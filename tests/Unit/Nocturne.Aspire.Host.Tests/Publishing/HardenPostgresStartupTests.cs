using FluentAssertions;
using Nocturne.Aspire.Host.Publishing;
using YamlDotNet.RepresentationModel;
using Xunit;
using static Nocturne.Aspire.Host.Tests.Publishing.ComposeYaml;

namespace Nocturne.Aspire.Host.Tests.Publishing;

public class HardenPostgresStartupTests : ComposeTransformTestBase
{
    private const string Compose = """
        services:
          nocturne-postgres:
            image: postgres:17.6
          nocturne-api:
            image: nocturne-api
            depends_on:
              nocturne-postgres:
                condition: service_started
              nocturne-cache:
                condition: service_started
          nocturne-cache:
            image: cache
        """;

    private static string? Condition(string compose, string service, string dependency)
    {
        var dependsOn = (YamlMappingNode)Service(compose, service)["depends_on"];
        return Text(((YamlMappingNode)dependsOn[dependency])["condition"]);
    }

    [Fact]
    public void PostgresGetsAReadinessHealthcheckOnItsTcpListener()
    {
        var result = PortainerComposePublisherExtensions.HardenPostgresStartup(Compose);

        var healthcheck = (YamlMappingNode)Service(result, Postgres)["healthcheck"];
        ((YamlSequenceNode)healthcheck["test"]).Children.Select(Text)
            .Should().Equal("CMD-SHELL", "pg_isready -h 127.0.0.1 -p 5432");
        Text(healthcheck["interval"]).Should().Be("5s");
        Text(healthcheck["timeout"]).Should().Be("5s");
        Text(healthcheck["retries"]).Should().Be("12");
        Text(healthcheck["start_period"]).Should().Be("60s");
        result.Should().Contain("retries: 12");
    }

    [Fact]
    public void ADependentWaitsForPostgresToBeHealthy()
    {
        var result = PortainerComposePublisherExtensions.HardenPostgresStartup(Compose);

        Condition(result, "nocturne-api", Postgres).Should().Be("service_healthy");
    }

    [Fact]
    public void ADependencyOtherThanPostgresKeepsItsCondition()
    {
        var result = PortainerComposePublisherExtensions.HardenPostgresStartup(Compose);

        Condition(result, "nocturne-api", "nocturne-cache").Should().Be("service_started");
    }

    [Fact]
    public void AnExistingHealthcheckIsKept()
    {
        var compose = Compose.Replace(
            "    image: postgres:17.6\n",
            "    image: postgres:17.6\n    healthcheck:\n      test: [\"CMD\", \"custom\"]\n");

        var result = PortainerComposePublisherExtensions.HardenPostgresStartup(compose);

        var healthcheck = (YamlMappingNode)Service(result, Postgres)["healthcheck"];
        ((YamlSequenceNode)healthcheck["test"]).Children.Select(Text).Should().Equal("CMD", "custom");
        healthcheck.Children.Should().ContainSingle();
    }

    [Fact]
    public void ApplyingItTwiceChangesNothingMore()
    {
        var once = PortainerComposePublisherExtensions.HardenPostgresStartup(Compose);

        PortainerComposePublisherExtensions.HardenPostgresStartup(once).Should().Be(once);
    }

    [Fact]
    public void AComposeWithoutPostgresIsUnchanged()
    {
        const string compose = """
            services:
              nocturne-api:
                image: nocturne-api
                depends_on:
                  nocturne-cache:
                    condition: service_started
            """;

        PortainerComposePublisherExtensions.HardenPostgresStartup(compose).Should().Be(compose);
    }
}
