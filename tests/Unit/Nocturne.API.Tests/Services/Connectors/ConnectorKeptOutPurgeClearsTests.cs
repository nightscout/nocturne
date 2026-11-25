using System.Reflection;
using FluentAssertions;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Core.Contracts.Connectors;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// What a purge that keeps a connector's data out clears of its state is declared on its
/// registration (<see cref="ConnectorRegistrationAttribute.KeptOutPurgeClears"/>). Every connector the
/// API ships is pinned here, so a connector that keeps more than its resume slot is a visible decision.
/// </summary>
public class ConnectorKeptOutPurgeClearsTests
{
    private static readonly Dictionary<string, ConnectorStateSlots> Expected = new()
    {
        ["carelink"] = ConnectorStateSlots.Resume,
        ["dexcom"] = ConnectorStateSlots.Resume,
        ["eversense"] = ConnectorStateSlots.Resume,
        ["glooko"] = ConnectorStateSlots.Resume,
        ["gluroo"] = ConnectorStateSlots.Resume,
        ["googlehealth"] = ConnectorStateSlots.None,
        ["homeassistant"] = ConnectorStateSlots.Resume,
        ["librelinkup"] = ConnectorStateSlots.Resume,
        ["myfitnesspal"] = ConnectorStateSlots.Resume,
        ["mylife"] = ConnectorStateSlots.Resume,
        ["nightscout"] = ConnectorStateSlots.Resume,
        ["nocturneremote"] = ConnectorStateSlots.Resume,
        ["oura"] = ConnectorStateSlots.Resume,
        ["tandem"] = ConnectorStateSlots.Resume,
        ["tidepool"] = ConnectorStateSlots.Resume,
        ["twiist"] = ConnectorStateSlots.Resume,
    };

    private static IEnumerable<ConnectorRegistrationAttribute> Registrations() =>
        Directory.GetFiles(AppContext.BaseDirectory, "Nocturne.Connectors.*.dll")
            .Where(path => !Path.GetFileName(path).Contains(".Tests", StringComparison.Ordinal))
            .Select(path => Assembly.Load(AssemblyName.GetAssemblyName(path)))
            .SelectMany(assembly => assembly.GetTypes())
            .Select(type => type.GetCustomAttribute<ConnectorRegistrationAttribute>(inherit: false))
            .OfType<ConnectorRegistrationAttribute>();

    [Fact]
    public void Every_connector_declares_what_a_purge_that_keeps_its_data_out_clears() =>
        Registrations()
            .ToDictionary(registration => registration.ConnectorId, registration => registration.KeptOutPurgeClears)
            .Should().BeEquivalentTo(Expected);

    [Fact]
    public void The_connector_metadata_carries_the_registrations_value()
    {
        _ = typeof(GoogleHealthConnectorConfiguration);

        ConnectorMetadataService.GetByConnectorId("googlehealth")!.KeptOutPurgeClears.Should().Be(ConnectorStateSlots.None);
    }

    [Theory]
    [InlineData("googlehealth", ConnectorStateSlots.Resume)]
    [InlineData("dexcom", ConnectorStateSlots.None)]
    [InlineData("dexcom", ConnectorStateSlots.Vendor)]
    public void A_purge_that_keeps_the_data_out_clears_what_the_metadata_declares_whatever_the_connector(
        string connector, ConnectorStateSlots declared)
    {
        var metadata = new ConnectorMetadataService.ConnectorDisplayInfo { ConnectorName = connector, KeptOutPurgeClears = declared };

        DataSourceService.PurgeClears(metadata, allowReimport: false).Should().Be(declared);
        DataSourceService.PurgeClears(metadata, allowReimport: true)
            .Should().Be(ConnectorStateSlots.Resume | ConnectorStateSlots.Vendor);
    }
}
