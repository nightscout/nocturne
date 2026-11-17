using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// Writes profile data back to the upstream Nightscout instance.
/// </summary>
public class NightscoutProfileWriteBackSink(
    HttpClient httpClient,
    IConnectorConfigurationLoader<NightscoutConnectorConfiguration> configLoader,
    NightscoutCircuitBreaker circuitBreaker,
    ILogger<NightscoutProfileWriteBackSink> logger)
    : NightscoutWriteBackSink<Profile>(httpClient, configLoader, circuitBreaker, logger)
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { LoopTargetRangeArrayConverter.ServeAsArrays } },
    };

    protected override JsonSerializerOptions SerializerOptions => Options;

    protected override string Endpoint => "/api/v1/profile";
}
