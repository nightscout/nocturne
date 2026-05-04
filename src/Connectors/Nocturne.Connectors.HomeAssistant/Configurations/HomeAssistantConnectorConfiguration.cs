using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.HomeAssistant.Configurations;

[ConnectorRegistration(
    "HomeAssistant",
    ServiceNames.HomeAssistantConnector,
    "HOME_ASSISTANT",
    "ConnectSource.HomeAssistant",
    "home-assistant-connector",
    "home-assistant",
    ConnectorCategory.Sync,
    "Connect via the Home Assistant integration (HACS)",
    "Home Assistant"
)]
public class HomeAssistantConnectorConfiguration : BaseConnectorConfiguration
{
    public HomeAssistantConnectorConfiguration()
    {
        ConnectSource = ConnectSource.HomeAssistant;
    }
}
