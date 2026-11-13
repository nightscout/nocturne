using System.Reflection;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// A connector's id keys its stored configuration and every sync trigger, and its data source id
/// is written on every record it imports. Changing either strands what is already stored, so each
/// is pinned here as a literal rather than read back from the constants that produce it.
/// </summary>
public class ConnectorIdentityTests
{
    private static readonly Dictionary<string, (string ConnectorId, string DataSourceId)> Expected = new()
    {
        ["CareLink"] = ("carelink", "carelink-connector"),
        ["Dexcom"] = ("dexcom", "dexcom-connector"),
        ["Eversense"] = ("eversense", "eversense-connector"),
        ["LibreLinkUp"] = ("librelinkup", "libre-connector"),
        ["Glooko"] = ("glooko", "glooko-connector"),
        ["Gluroo"] = ("gluroo", "gluroo-connector"),
        ["GoogleHealth"] = ("googlehealth", "google-health-connector"),
        ["HomeAssistant"] = ("homeassistant", "home-assistant-connector"),
        ["MyFitnessPal"] = ("myfitnesspal", "myfitnesspal-connector"),
        ["MyLife"] = ("mylife", "mylife-connector"),
        ["Nightscout"] = ("nightscout", "nightscout-connector"),
        ["NocturneRemote"] = ("nocturneremote", "nocturne-remote-connector"),
        ["Tandem"] = ("tandem", "tconnectsync-connector"),
        ["Tidepool"] = ("tidepool", "tidepool-connector"),
        ["Twiist"] = ("twiist", "twiist-connector"),
    };

    [Fact]
    public void EveryConnector_KeepsItsStoredIdentity()
    {
        var registered = ConnectorInstallers.Types()
            .Select(t => t.GetCustomAttribute<ConnectorRegistrationAttribute>(inherit: false))
            .OfType<ConnectorRegistrationAttribute>()
            .DistinctBy(r => r.ConnectorName)
            .ToDictionary(r => r.ConnectorName, r => (r.ConnectorId, r.DataSourceId));

        registered.Should().BeEquivalentTo(Expected);
    }

    [Fact]
    public void DocumentationUrls_AreTheRegisteredSetupGuides()
    {
        _ = ConnectorInstallers.Types();
        var urls = Expected.Values.ToDictionary(
            identity => identity.ConnectorId,
            identity => ConnectorMetadataService.GetRegistrationByConnectorId(identity.ConnectorId)?.DocumentationUrl);

        urls.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["carelink"] = null,
            ["dexcom"] = "https://nightscoutfoundation.org/nocturne/docs/connectors/dexcom",
            ["eversense"] = null,
            ["librelinkup"] = "https://nightscoutfoundation.org/nocturne/docs/connectors/libre",
            ["glooko"] = "https://nightscoutfoundation.org/nocturne/docs/connectors/glooko",
            ["gluroo"] = null,
            ["googlehealth"] = null,
            ["homeassistant"] = null,
            ["myfitnesspal"] = null,
            ["mylife"] = null,
            ["nightscout"] = null,
            ["nocturneremote"] = null,
            ["tandem"] = null,
            ["tidepool"] = null,
            ["twiist"] = null,
        });
    }
}
