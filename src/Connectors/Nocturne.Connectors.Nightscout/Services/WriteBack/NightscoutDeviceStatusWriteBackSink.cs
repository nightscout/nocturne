using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// Writes device status data back to the upstream Nightscout instance.
/// </summary>
/// <remarks>
/// A status goes upstream under a stable 24-hex <c>_id</c>, and Nightscout inserts statuses with an
/// ordered <c>insertMany</c> (<c>insertOne</c> up to 15.0.6), so a status sent again, such as an
/// uploader's re-upload of it, is refused with MongoDB's duplicate-key error <c>E11000</c> and stops
/// the statuses after it. That refusal means the status is already there, so the batch is sent again
/// one status at a time and a status refused that way on its own counts as written.
/// </remarks>
public class NightscoutDeviceStatusWriteBackSink(
    HttpClient httpClient,
    IConnectorConfigurationLoader<NightscoutConnectorConfiguration> configLoader,
    NightscoutCircuitBreaker circuitBreaker,
    ILogger<NightscoutDeviceStatusWriteBackSink> logger)
    : NightscoutWriteBackSink<DeviceStatus>(httpClient, configLoader, circuitBreaker, logger)
{
    protected override JsonSerializerOptions SerializerOptions => UpstreamIdentityJson.Options;

    protected override string Endpoint => "/api/v1/devicestatus";

    protected override bool IsAlreadyStored(string refusal)
        => refusal.Contains("E11000", StringComparison.Ordinal);
}
