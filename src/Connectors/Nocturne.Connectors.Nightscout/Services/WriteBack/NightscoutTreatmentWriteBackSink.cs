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
/// <remarks>
/// <para>
/// A treatment goes upstream under <see cref="UpstreamIdentityJson.TreatmentWireKey"/> as both
/// <c>_id</c> and <c>identifier</c>, the shape every earlier write-back used, and is created by POST.
/// </para>
/// <para>
/// An edit asks the upstream first whether it holds a copy under that <c>identifier</c>
/// (<see cref="HoldsIdentifierAsync"/>).
/// <list type="bullet">
/// <item>If it does, the edit is POSTed in the same shape. From 15.0.7 the upsert matches the copy
/// by its <c>identifier</c>. Up to 15.0.6 a POST upserts by <c>created_at</c> and
/// <c>eventType</c>, which finds the copy an earlier POST stored under the string <c>_id</c>; an
/// edit that moved either field finds nothing, and inserting it again under that <c>_id</c> is a
/// duplicate-key error, so it is refused rather than stored twice. A PUT would not do: up to
/// 15.0.6 it saves under <c>new ObjectID(_id)</c>, which never equals that string, so it inserts a
/// second copy.</item>
/// <item>If it does not, the treatment is one the upstream holds under its <c>_id</c> alone, such as
/// the original of a record a Nightscout migration imported, or holds nowhere. It is PUT with no
/// <c>identifier</c>, which every version saves by that <c>_id</c>: in place, or as the one copy.
/// With an <c>identifier</c>, 15.0.7 and later would match neither and store a second copy.</item>
/// <item>If it cannot say, nothing is sent and the failure counts against the circuit breaker.
/// Either write could store a second copy: the POST of a treatment upstream holds under its
/// <c>_id</c> alone, the PUT of one held under its <c>identifier</c>.</item>
/// </list>
/// </para>
/// </remarks>
public class NightscoutTreatmentWriteBackSink(
    HttpClient httpClient,
    IConnectorConfigurationLoader<NightscoutConnectorConfiguration> configLoader,
    NightscoutCircuitBreaker circuitBreaker,
    ILogger<NightscoutTreatmentWriteBackSink> logger)
    : NightscoutWriteBackSink<Treatment>(httpClient, configLoader, circuitBreaker, logger)
{
    protected override JsonSerializerOptions SerializerOptions => UpstreamIdentityJson.Options;

    protected override string Endpoint => "/api/v1/treatments";

    protected override bool ShouldSkip(Treatment item)
        => item.DataSource == DataSources.NightscoutConnector;

    protected override async Task SendUpdatedAsync(
        NightscoutConnectorConfiguration config, Treatment item, CancellationToken ct)
    {
        if (UpstreamIdentityJson.TreatmentWireKey(item) is not { } key)
        {
            await SendAsync(config, HttpMethod.Post, new[] { item }, SerializerOptions, ct);
            return;
        }

        switch (await HoldsIdentifierAsync(config, key, ct))
        {
            case true:
                await SendAsync(config, HttpMethod.Post, new[] { item }, SerializerOptions, ct);
                break;
            case false:
                await SendAsync(config, HttpMethod.Put, item, UpstreamIdentityJson.IdOnlyOptions, ct);
                break;
        }
    }

    /// <summary>
    /// The lower <c>created_at</c> bound of the identifier lookup. Nightscout's
    /// <c>lib/server/query.js</c> (<c>enforceDateFilter</c>) narrows every treatment find to
    /// <c>created_at</c> within the last four days unless the find names <c>_id</c>,
    /// <c>created_at</c> or <c>dateString</c>; <c>noDateFilter</c> is not reachable from the query
    /// string. The bound is not taken around the treatment's own time because an edit may have moved
    /// it, and the copy upstream still carries the old one. Nightscout writes <c>created_at</c> as an
    /// ISO-8601 string, which compares as a string at or above this one.
    /// </summary>
    private const string LookupCreatedAtFloor = "1970-01-01T00:00:00.000Z";

    /// <returns>
    /// Whether the upstream holds a treatment under <paramref name="identifier"/>, or null when it
    /// could not say: the read failed (counted against the breaker by <see cref="NightscoutWriteBackSink{T}.GetAsync"/>)
    /// or its body is not an array (counted here).
    /// </returns>
    private async Task<bool?> HoldsIdentifierAsync(
        NightscoutConnectorConfiguration config, string identifier, CancellationToken ct)
    {
        var body = await GetAsync(
            config,
            $"{Endpoint}.json?find[identifier]={Uri.EscapeDataString(identifier)}"
            + $"&find[created_at][$gte]={Uri.EscapeDataString(LookupCreatedAtFloor)}&count=1",
            ct);
        if (body is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                return document.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
        }

        RecordFailure(HttpMethod.Get, null);
        return null;
    }
}
