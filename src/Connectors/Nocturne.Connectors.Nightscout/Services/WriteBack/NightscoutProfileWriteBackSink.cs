using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// Writes profile data back to the upstream Nightscout instance.
/// </summary>
public class NightscoutProfileWriteBackSink(
    HttpClient httpClient,
    IConnectorConfigurationLoader<NightscoutConnectorConfiguration> configLoader,
    IServiceProvider serviceProvider,
    NightscoutCircuitBreaker circuitBreaker,
    ILogger<NightscoutProfileWriteBackSink> logger)
    : NightscoutWriteBackSink<Profile>(httpClient, configLoader, serviceProvider, circuitBreaker, logger)
{
    protected override string Endpoint => "/api/v1/profile";
}
