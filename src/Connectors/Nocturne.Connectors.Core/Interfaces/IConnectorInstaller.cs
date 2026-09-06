using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Implemented by each connector package to register its services with DI.
///     Discovered automatically at startup via assembly scanning.
/// </summary>
public interface IConnectorInstaller
{
    /// <summary>
    ///     Registers all services required by this connector.
    /// </summary>
    void Install(IServiceCollection services, IConfiguration configuration);
}
