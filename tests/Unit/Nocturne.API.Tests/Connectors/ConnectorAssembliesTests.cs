using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// The connector scan returns every connector assembly of the base directory, whatever the process has
/// loaded, and the registry built from it knows every shipped connector. The registry is a latched
/// static and other tests load the connector assemblies first, so this guards the contract of
/// <see cref="ConnectorAssemblies.Load"/> and what the registry reads, and cannot reproduce a load order.
/// </summary>
public class ConnectorAssembliesTests
{
    [Fact]
    public void Load_ReturnsEveryConnectorAssemblyOfTheBaseDirectoryWhateverIsLoaded()
    {
        var onDisk = Directory.GetFiles(AppContext.BaseDirectory, "Nocturne.Connectors.*.dll")
            .Select(Path.GetFileNameWithoutExtension);

        ConnectorAssemblies.Load().Select(a => a.GetName().Name)
            .Should().Contain(onDisk);
    }

    [Theory]
    [InlineData("dexcom")]
    [InlineData("glooko")]
    [InlineData("tandem")]
    [InlineData("tidepool")]
    public void TheRegistry_KnowsEveryShippedConnector(string connectorId)
    {
        ConnectorMetadataService.GetByConnectorId(connectorId).Should().NotBeNull();
        ConnectorMetadataService.GetRegistrationByConnectorId(connectorId).Should().NotBeNull();
        ConnectorMetadataService.GetConfigurationTypeByConnectorId(connectorId).Should().NotBeNull();
    }

    [Fact]
    public void TheRegistry_ReadsAConnectorsOwnStaleThreshold()
    {
        ConnectorMetadataService.GetByConnectorId("glooko")!.DefaultStaleThresholdMinutes
            .Should().BeGreaterThan(180);
    }

    [Fact]
    public void TheConfigurationType_IsTheOneThatDeclaresTheRegistration()
    {
        var type = ConnectorMetadataService.GetConfigurationTypeByConnectorId("Glooko");

        type.Should().NotBeNull();
        ConnectorRegistrationAttribute.DeclaredOn(type!).ConnectorId.Should().Be("glooko");
    }
}
