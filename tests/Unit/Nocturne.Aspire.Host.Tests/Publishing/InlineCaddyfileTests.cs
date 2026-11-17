using FluentAssertions;
using Nocturne.Aspire.Host.Publishing;
using YamlDotNet.RepresentationModel;
using Xunit;
using static Nocturne.Aspire.Host.Tests.Publishing.ComposeYaml;

namespace Nocturne.Aspire.Host.Tests.Publishing;

public class InlineCaddyfileTests : ComposeTransformTestBase
{
    private const string Compose = """
        services:
          caddy:
            image: caddy:2
            volumes:
            - type: bind
              source: ./caddy/Caddyfile
              target: /etc/caddy/Caddyfile
              read_only: true
            - type: volume
              source: caddy-data
              target: /data
          nocturne-api:
            image: nocturne-api
        configs:
          nocturne-init:
            content: "kept"
        """;

    private const string Caddyfile = "{$BASE_DOMAIN} {\n}\n";

    [Fact]
    public void TheCaddyfileBindMountIsReplacedByAConfigsReference()
    {
        var result = PortainerComposePublisherExtensions.InlineCaddyfile(Compose, WriteFile("Caddyfile", Caddyfile));

        var reference = ConfigReference(Service(result, "caddy"));
        Text(reference["source"]).Should().Be("nocturne-caddyfile");
        Text(reference["target"]).Should().Be("/etc/caddy/Caddyfile");
        Text(reference["mode"]).Should().Be("420");
        result.Should().Contain("mode: 420");
        ((YamlSequenceNode)Service(result, "caddy")["volumes"]).Children
            .Select(volume => Text(((YamlMappingNode)volume)["source"]))
            .Should().Equal("caddy-data");
    }

    [Fact]
    public void TheCaddyfileIsInlinedWithItsDollarsEscapedForCompose()
    {
        var result = PortainerComposePublisherExtensions.InlineCaddyfile(Compose, WriteFile("Caddyfile", Caddyfile));

        Text(((YamlMappingNode)Configs(result)["nocturne-caddyfile"])["content"])
            .Should().Be("{$$BASE_DOMAIN} {\n}\n");
    }

    [Fact]
    public void ConfigsAnotherTransformAddedAreKept()
    {
        var result = PortainerComposePublisherExtensions.InlineCaddyfile(Compose, WriteFile("Caddyfile", Caddyfile));

        Text(((YamlMappingNode)Configs(result)["nocturne-init"])["content"]).Should().Be("kept");
    }

    [Fact]
    public void OtherServicesAreUnchanged()
    {
        var result = PortainerComposePublisherExtensions.InlineCaddyfile(Compose, WriteFile("Caddyfile", Caddyfile));

        Text(Service(result, "nocturne-api")["image"]).Should().Be("nocturne-api");
        Service(result, "nocturne-api").Children.Should().ContainSingle();
    }

    [Fact]
    public void ApplyingItTwiceChangesNothingMore()
    {
        var caddyfile = WriteFile("Caddyfile", Caddyfile);
        var once = PortainerComposePublisherExtensions.InlineCaddyfile(Compose, caddyfile);

        PortainerComposePublisherExtensions.InlineCaddyfile(once, caddyfile).Should().Be(once);
    }

    [Fact]
    public void AComposeWithoutCaddyIsUnchanged()
    {
        const string compose = """
            services:
              nocturne-api:
                image: nocturne-api
            """;

        PortainerComposePublisherExtensions.InlineCaddyfile(compose, "missing").Should().Be(compose);
    }

    [Fact]
    public void ACaddyfileThatIsNeededButMissingFails()
    {
        var act = () => PortainerComposePublisherExtensions.InlineCaddyfile(
            Compose, Path.Combine(Path.GetTempPath(), "nocturne-no-such-Caddyfile"));

        act.Should().Throw<FileNotFoundException>();
    }
}
