using FluentAssertions;
using Nocturne.Core.Constants;
using YamlDotNet.RepresentationModel;
using Xunit;
using static Nocturne.Aspire.Host.Tests.Publishing.ShippedBundleTests;

namespace Nocturne.Aspire.Host.Tests.Publishing;

/// <summary>
/// The hand-written first-boot files in <c>deploy/vm</c> against the bundle
/// <c>scripts/publish-release.cs</c> generates next to them, and the names they share with the
/// setup service.
/// </summary>
public class VmBundleTests
{
    private const string Compose = "deploy/vm/bundle/docker-compose.yaml";

    [Fact]
    public void TheSetupPageRunsOnlyUnderItsProfile()
    {
        Profiles("selfhost-setup").Should().Equal(ServiceNames.SelfHost.SetupProfile);
    }

    [Fact]
    public void TheDuckDnsUpdaterRunsOnlyUnderItsProfile()
    {
        Profiles("duckdns").Should().Equal(ServiceNames.SelfHost.DuckDnsProfile);
    }

    [Fact]
    public void TheSetupUnitRunsTheBundlesSetupService()
    {
        Read("deploy/vm/systemd/nocturne-setup.service")
            .Should().Contain("docker compose run --rm --service-ports selfhost-setup");
    }

    [Theory]
    [InlineData("deploy/vm/systemd/nocturne-setup.service", "ConditionPathExists=!/opt/nocturne/")]
    [InlineData("deploy/vm/systemd/nocturne.service", "ConditionPathExists=/opt/nocturne/")]
    public void TheUnitsGateOnTheMarkerTheSetupServiceWrites(string unit, string condition)
    {
        Read(unit).Should().Contain(condition + ServiceNames.SelfHost.CompletionMarker);
    }

    [Fact]
    public void EveryGeneratedSecretIsAVariableTheBundleReads()
    {
        var secrets = Lines("deploy/vm/bundle/generated-secrets");
        var variables = Lines("deploy/vm/bundle/.env.example")
            .Where(line => !line.StartsWith('#') && line.Contains('='))
            .Select(line => line[..line.IndexOf('=')]);

        secrets.Should().Contain("INSTANCE_KEY").And.BeSubsetOf(variables);
    }

    [Fact]
    public void TheFirstBootScriptFillsTheSetupVariables()
    {
        var variables = Lines("deploy/vm/bundle/.env.example").Select(line => line.Split('=')[0]);
        var script = Read("deploy/vm/firstboot.sh");

        variables.Should().Contain(["SETUP_CLAIM_CODE", "PUBLIC_IP"]);
        script.Should().Contain("set_env SETUP_CLAIM_CODE").And.Contain("set_env PUBLIC_IP");
    }

    private static IEnumerable<string> Profiles(string service) =>
        ((YamlSequenceNode)Service(Compose, service)["profiles"]).Select(node => ((YamlScalarNode)node).Value!);

    private static string Read(string path) => File.ReadAllText(Path.Combine(RepositoryRoot(), path));

    private static string[] Lines(string path) =>
        Read(path).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
