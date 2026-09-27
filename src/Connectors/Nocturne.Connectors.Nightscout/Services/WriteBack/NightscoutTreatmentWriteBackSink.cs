using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Serializers;

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
    protected override JsonSerializerOptions SerializerOptions => UpstreamTreatmentJson;

    protected override string Endpoint => "/api/v1/treatments";

    protected override bool ShouldSkip(Treatment item)
        => item.DataSource == DataSources.NightscoutConnector;

    /// <summary>
    /// Writes <c>_id</c> and <c>identifier</c> from the treatment's legacy id rather than the id it is
    /// served by, still coerced to 24 hex: Nightscout replaces a non-ObjectId <c>_id</c> with one of
    /// its own (<c>normalizeTreatmentId</c>) and older versions fail to look it up.
    /// </summary>
    internal static JsonSerializerOptions UpstreamTreatmentJson { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { KeyByLegacyId } },
    };

    private static void KeyByLegacyId(JsonTypeInfo info)
    {
        if (info.Type != typeof(Treatment))
            return;

        foreach (var property in info.Properties.Where(p => p.CustomConverter is ObjectIdJsonConverter))
        {
            var served = property.Get!;
            property.Get = treatment => ((Treatment)treatment).LegacyId ?? served(treatment);
        }
    }
}
