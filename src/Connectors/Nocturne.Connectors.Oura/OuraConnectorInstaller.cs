using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Oura.Configurations;

namespace Nocturne.Connectors.Oura;

public class OuraConnectorInstaller : IConnectorInstaller
{
    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        services.AddConnectorConfiguration<OuraConnectorConfiguration>(configuration);
    }
}
