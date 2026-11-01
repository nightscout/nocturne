using FluentAssertions;
using Nocturne.Aspire.Host.Publishing;
using YamlDotNet.RepresentationModel;
using Xunit;
using static Nocturne.Aspire.Host.Tests.Publishing.ComposeYaml;

namespace Nocturne.Aspire.Host.Tests.Publishing;

public class InlineInitScriptTests : ComposeTransformTestBase
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
            - type: volume
              source: nocturne-postgres-data
              target: /var/lib/postgresql/data
          nocturne-api:
            image: nocturne-api
            environment:
              ASPNETCORE_ENVIRONMENT: "Production"
        """;

    private const string Script = "#!/bin/bash\necho \"${POSTGRES_DB:?unset}\"\n";

    [Fact]
    public void TheInitBindMountIsReplacedByAConfigsReference()
    {
        var result = PortainerComposePublisherExtensions.InlineInitScript(Compose, WriteFile("00-init.sh", Script));

        var reference = ConfigReference(Service(result, Postgres));
        Text(reference["source"]).Should().Be("nocturne-init");
        Text(reference["target"]).Should().Be("/docker-entrypoint-initdb.d/00-init.sh");
        Text(reference["mode"]).Should().Be("493");
        result.Should().Contain("mode: 493");
        ((YamlSequenceNode)Service(result, Postgres)["volumes"]).Children
            .Select(volume => Text(((YamlMappingNode)volume)["source"]))
            .Should().Equal("nocturne-postgres-data");
    }

    [Fact]
    public void TheScriptIsInlinedWithItsDollarsEscapedForCompose()
    {
        var result = PortainerComposePublisherExtensions.InlineInitScript(Compose, WriteFile("00-init.sh", Script));

        Text(((YamlMappingNode)Configs(result)["nocturne-init"])["content"])
            .Should().Be("#!/bin/bash\necho \"$${POSTGRES_DB:?unset}\"\n");
    }

    [Fact]
    public void OtherServicesAreUnchanged()
    {
        var result = PortainerComposePublisherExtensions.InlineInitScript(Compose, WriteFile("00-init.sh", Script));

        Text(((YamlMappingNode)Service(result, "nocturne-api")["environment"])["ASPNETCORE_ENVIRONMENT"])
            .Should().Be("Production");
        Text(Service(result, "nocturne-api")["image"]).Should().Be("nocturne-api");
        Service(result, "nocturne-api").Children.Should().HaveCount(2);
    }

    [Fact]
    public void ApplyingItTwiceChangesNothingMore()
    {
        var init = WriteFile("00-init.sh", Script);
        var once = PortainerComposePublisherExtensions.InlineInitScript(Compose, init);

        PortainerComposePublisherExtensions.InlineInitScript(once, init).Should().Be(once);
    }

    [Fact]
    public void AComposeWithoutPostgresIsUnchanged()
    {
        const string compose = """
            services:
              nocturne-api:
                image: nocturne-api
            """;

        PortainerComposePublisherExtensions.InlineInitScript(compose, "missing.sh").Should().Be(compose);
    }

    [Fact]
    public void AnInitScriptThatIsNeededButMissingFails()
    {
        var act = () => PortainerComposePublisherExtensions.InlineInitScript(
            Compose, Path.Combine(Path.GetTempPath(), "nocturne-no-such-init.sh"));

        act.Should().Throw<FileNotFoundException>();
    }
}
