using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.HomeAssistant.Configurations;

namespace Nocturne.Connectors.HomeAssistant;

public class HomeAssistantConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "HomeAssistant";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        services.AddConnectorConfiguration<HomeAssistantConnectorConfiguration>(
            configuration, "HomeAssistant");
    }
}
