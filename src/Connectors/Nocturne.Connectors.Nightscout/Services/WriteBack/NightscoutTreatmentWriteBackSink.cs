using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// Writes treatment data back to the upstream Nightscout instance.
/// Skips treatments that originated from the Nightscout connector to prevent sync loops.
/// </summary>
public class NightscoutTreatmentWriteBackSink(
    HttpClient httpClient,
    IConnectorConfigurationLoader<NightscoutConnectorConfiguration> configLoader,
    NightscoutCircuitBreaker circuitBreaker,
    ILogger<NightscoutTreatmentWriteBackSink> logger)
    : NightscoutWriteBackSink<Treatment>(httpClient, configLoader, circuitBreaker, logger)
{
    protected override string Endpoint => "/api/v1/treatments";

    protected override JsonSerializerOptions SerializerOptions => TreatmentWriteBackJson.Options;

    protected override bool ShouldSkip(Treatment item)
        => item.DataSource == DataSources.NightscoutConnector;

    protected override async Task AfterWrittenAsync(
        NightscoutConnectorConfiguration config, IReadOnlyList<Treatment> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (PreBolusSplitCopy.CreatedAt(item) is not { } createdAt)
                continue;

            var found = await ExchangeAsync(config, HttpMethod.Get, PreBolusSplitCopy.Query(Endpoint, item, createdAt), ct);
            if (found is null)
                continue;

            foreach (var id in PreBolusSplitCopy.Ids(found, item, createdAt))
                await ExchangeAsync(config, HttpMethod.Delete, $"{Endpoint}/{Uri.EscapeDataString(id)}", ct);
        }
    }
}
