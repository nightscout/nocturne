using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// Writes device status data back to the upstream Nightscout instance.
/// </summary>
public class NightscoutDeviceStatusWriteBackSink(
    HttpClient httpClient,
    IConnectorConfigurationLoader<NightscoutConnectorConfiguration> configLoader,
    NightscoutCircuitBreaker circuitBreaker,
    ILogger<NightscoutDeviceStatusWriteBackSink> logger)
    : NightscoutWriteBackSink<DeviceStatus>(httpClient, configLoader, circuitBreaker, logger)
{
    protected override string Endpoint => "/api/v1/devicestatus";
}
