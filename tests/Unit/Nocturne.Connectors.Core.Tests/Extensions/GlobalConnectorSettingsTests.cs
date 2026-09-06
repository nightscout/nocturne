using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Extensions;

/// <summary>
///     <c>Enabled</c> decides both whether a connector installs and whether it is polled, from two
///     readers. A global switch honoured by one and not the other leaves the connector installed
///     with a working manual sync and a live executor while every poller stands down.
/// </summary>
public class GlobalConnectorSettingsTests
{
    [Theory]
    [InlineData("Parameters:Connectors:Settings:Enabled")]
    [InlineData("Connectors:Settings:Enabled")]
    public void DisablingConnectorsGlobally_DisablesTheBoundConfiguration(string key)
    {
        Bound((key, "false")).Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("Parameters:Connectors:Settings", "Parameters:Connectors:Test")]
    [InlineData("Connectors:Settings", "Connectors:Test")]
    public void AConnectorEnabledInItsOwnSection_OutranksAGlobalFalse(string global, string connector)
    {
        Bound(($"{global}:Enabled", "false"), ($"{connector}:Enabled", "true"))
            .Enabled.Should().BeTrue();
    }

    /// <summary>
    ///     <c>Enabled</c>, <c>BatchSize</c> and <c>SyncIntervalMinutes</c> at the configuration root
    ///     name no connector, so an unrelated setting of that name reached every connector.
    /// </summary>
    [Fact]
    public void RootLevelSettings_ReachNoConnector()
    {
        var config = Bound(
            ("Enabled", "false"),
            ("BatchSize", "17"),
            ("SyncIntervalMinutes", "23"));

        config.Enabled.Should().BeTrue();
        config.BatchSize.Should().Be(new TestConnectorConfiguration().BatchSize);
        config.SyncIntervalMinutes.Should().Be(new TestConnectorConfiguration().SyncIntervalMinutes);
    }

    [Theory]
    [InlineData("Parameters:Connectors:Settings")]
    [InlineData("Connectors:Settings")]
    public void GlobalDefaults_ApplyToConnectorsThatDoNotOverrideThem(string global)
    {
        Bound(($"{global}:BatchSize", "17")).BatchSize.Should().Be(17);
    }

    /// <summary>
    ///     Aspire writes the <c>Parameters:</c>-prefixed copy, so where both are present that one
    ///     is the operator's live setting and the bare section is what it was layered over.
    /// </summary>
    [Fact]
    public void WithBothCopiesOfTheGlobalSection_TheParametersPrefixedOneWins()
    {
        Bound(
            ("Parameters:Connectors:Settings:Enabled", "true"),
            ("Connectors:Settings:Enabled", "false")).Enabled.Should().BeTrue();
    }

    /// <summary>
    ///     <c>TimezoneOffset</c> is the one root key with a producer: the Aspire generator emits
    ///     <c>connector.WithEnvironment("TimezoneOffset", …)</c> per connector resource, where the
    ///     unprefixed name addresses that connector alone.
    /// </summary>
    [Fact]
    public void RootLevelTimezoneOffset_StillReachesTheConnector()
    {
        Bound(("TimezoneOffset", "-5.5")).TimezoneOffset.Should().Be(-5.5);
    }

    private static TestConnectorConfiguration Bound(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var config = new TestConnectorConfiguration();
        configuration.BindConnectorConfiguration(config, "Test");
        return config;
    }

    private sealed class TestConnectorConfiguration : BaseConnectorConfiguration
    {
        protected override void ValidateSourceSpecificConfiguration() { }
    }
}
