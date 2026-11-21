using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Utilities;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Utilities;

public class ConnectorLegacyIdsTests
{
    [ConnectorRegistration("Declared", "declared", "DECLARED", "Declared", "declared-connector", LegacyIdPrefix = "declared")]
    private sealed class DeclaredConfig : BaseConnectorConfiguration;

    [ConnectorRegistration("Sourced", "sourced", "SOURCED", "Sourced", "sourced-connector")]
    private sealed class SourcedConfig : BaseConnectorConfiguration;

    [ConnectorRegistration("Unsourced", "unsourced", "UNSOURCED", "Unsourced")]
    private sealed class UnsourcedConfig : BaseConnectorConfiguration;

    [Fact]
    public void Of_JoinsThePrefixAndThePartsWithUnderscores()
    {
        new ConnectorLegacyIds("tandem").Of("usermode", 456961L, 456965L)
            .Should().Be("tandem_usermode_456961_456965");
    }

    [Fact]
    public void Of_WritesAMissingPartAsEmpty()
    {
        new ConnectorLegacyIds("glooko").Of("kind", null, "x").Should().Be("glooko_kind__x");
    }

    [Fact]
    public void Hashed_IsThePrefixAndTheSha1OfThePrefixedParts()
    {
        var ids = new ConnectorLegacyIds("glooko");

        ids.Hashed("ssv2_note", 123L, null).Should().Be("glooko_e70bfa5ddb335f949654b96d9b59d1b637c81ef3");
        ids.Hashed("ssv2_note", 123L, "felt low").Should().Be("glooko_843d731bd29e619dc85e3e8e52afb6e61c215948");
    }

    [Fact]
    public void For_ReadsTheDeclaredPrefix()
    {
        ConnectorLegacyIds.For<DeclaredConfig>().Prefix.Should().Be("declared");
    }

    [Fact]
    public void For_FallsBackToTheDataSourceIdWhenNoPrefixIsDeclared()
    {
        ConnectorLegacyIds.For<SourcedConfig>().Prefix.Should().Be("sourced-connector");
    }

    [Fact]
    public void For_RefusesARegistrationWithNeitherAPrefixNorADataSource()
    {
        FluentActions.Invoking(ConnectorLegacyIds.For<UnsourcedConfig>)
            .Should().Throw<InvalidOperationException>();
    }
}
