using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// Writes entry data back to the upstream Nightscout instance.
/// Skips entries that originated from the Nightscout connector to prevent sync loops.
/// </summary>
public class NightscoutEntryWriteBackSink(
    HttpClient httpClient,
    IConnectorConfigurationLoader<NightscoutConnectorConfiguration> configLoader,
    NightscoutCircuitBreaker circuitBreaker,
    ILogger<NightscoutEntryWriteBackSink> logger)
    : NightscoutWriteBackSink<Entry>(httpClient, configLoader, circuitBreaker, logger)
{
    protected override string Endpoint => "/api/v1/entries";

    protected override bool ShouldSkip(Entry item)
        => item.DataSource == DataSources.NightscoutConnector;
}
