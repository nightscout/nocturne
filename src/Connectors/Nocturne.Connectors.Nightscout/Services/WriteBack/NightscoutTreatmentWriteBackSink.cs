using System.Text;
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
/// A treatment is created by a POST carrying <see cref="UpstreamIdentityJson.TreatmentWireKey"/>
/// as both <c>_id</c> and <c>identifier</c>.
/// </para>
/// <para>
/// An edit is written onto the copy the upstream already holds, looked for in this order.
/// <list type="number">
/// <item>Under an <c>identifier</c> any write-back may have used
/// (<see cref="UpstreamIdentityJson.TreatmentWireForms"/>). The edit carries the one found, which
/// every version stores as sent and 15.0.7 and later upsert by. Where the copy is held:
/// <list type="bullet">
/// <item>Under an ObjectId other than that identifier (every copy 15.0.7 and later minted): a PUT
/// under it. 15.0.7 and later match the identifier; up to 15.0.6 <c>save()</c> replaces the
/// document by its ObjectId, wherever the edit moved it.</item>
/// <item>Under that identifier as its <c>_id</c>: a string, as up to 15.0.6 a POST stores it, or
/// an ObjectId, as up to 15.0.6 a PUT saves it. The read serves both alike, so
/// <c>find[_id]</c>, which matches only an ObjectId, tells them apart. An ObjectId takes the PUT
/// above. A string takes a POST under it: up to 15.0.6 a POST upserts by <c>created_at</c> and
/// <c>eventType</c>, and an edit that moved either is refused as a duplicate key rather than
/// stored twice, where a PUT, saving under <c>new ObjectID(_id)</c>, would store a second
/// copy.</item>
/// <item>Under any other string <c>_id</c>: a POST without one, upserted as above.</item>
/// </list></item>
/// <item>Failing that, under <c>_id</c> = the wire key, as the original of a record a Nightscout
/// migration imported is. <c>find[_id]</c> names <c>_id</c>, so it skips the four-day window. The
/// edit is PUT under that <c>_id</c>, which every version saves by it, with the identifier the
/// copy holds if it has one: a PUT replaces the whole document, and a v3 client such as AAPS
/// matches its records by it.</item>
/// <item>Held nowhere: the create's POST. An edit PUT under its <c>_id</c> alone would be stored by
/// 15.0.7 and later under an ObjectId with no identifier, which a later create's identifier upsert
/// never matches: a second copy.</item>
/// </list>
/// A lookup that cannot say sends nothing and counts against the circuit breaker: either write
/// could store a second copy. The edit is not retried, so upstream keeps the previous version until
/// the treatment is edited again.
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

        var forms = UpstreamIdentityJson.TreatmentWireForms(item);
        var byIdentifier = await FindAsync(config, IdentifierQuery(forms), ct);
        if (byIdentifier is null)
            return;

        if (Preferred(byIdentifier, forms) is { } copy)
        {
            var identifier = copy.Identifier!;
            // A read serves an ObjectId as lowercase hex, so any other _id is a string.
            if (!MongoObjectId.IsObjectId(copy.Id))
            {
                var stringId = copy.Id == identifier ? identifier : null;
                await PostAsync(config, item, stringId, identifier, ct);
                return;
            }

            if (copy.Id == identifier)
            {
                var underObjectId = await FindAsync(config, IdQuery(copy.Id), ct);
                if (underObjectId is null)
                    return;
                if (underObjectId.Count == 0)
                {
                    await PostAsync(config, item, identifier, identifier, ct);
                    return;
                }
            }

            await PutAsync(config, item, copy.Id, identifier, ct);
            return;
        }

        var byId = await FindAsync(config, IdQuery(key), ct);
        if (byId is null)
            return;

        if (byId.FirstOrDefault() is { } original)
            await PutAsync(config, item, key, original.Identifier, ct);
        else
            await PostAsync(config, item, key, key, ct);
    }

    private Task PostAsync(NightscoutConnectorConfiguration config, Treatment item, string? id, string identifier, CancellationToken ct)
        => SendAsync(config, HttpMethod.Post, new[] { UpstreamIdentityJson.TreatmentPayload(item, id, identifier) }, null, ct);

    private Task PutAsync(NightscoutConnectorConfiguration config, Treatment item, string id, string? identifier, CancellationToken ct)
        => SendAsync(config, HttpMethod.Put, UpstreamIdentityJson.TreatmentPayload(item, id, identifier), null, ct);

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

    /// <summary>At most this many copies are read back; more than one is already a duplicate.</summary>
    private const int LookupCount = 10;

    private string IdentifierQuery(IReadOnlyList<string> forms)
    {
        var query = new StringBuilder($"{Endpoint}.json?");
        for (var i = 0; i < forms.Count; i++)
            query.Append($"find[identifier][$in][{i}]={Uri.EscapeDataString(forms[i])}&");
        return query.Append($"find[created_at][$gte]={Uri.EscapeDataString(LookupCreatedAtFloor)}&count={LookupCount}").ToString();
    }

    private string IdQuery(string objectId) => $"{Endpoint}.json?find[_id]={Uri.EscapeDataString(objectId)}&count=1";

    /// <summary>The copy held under the earliest of <paramref name="forms"/> any copy is held under.</summary>
    private static UpstreamCopy? Preferred(IReadOnlyList<UpstreamCopy> copies, IReadOnlyList<string> forms) =>
        forms.Select(form => copies.FirstOrDefault(c => c.Identifier == form)).FirstOrDefault(c => c is not null);

    /// <summary>A copy upstream: the <c>_id</c> the read serves it by, and its <c>identifier</c>.</summary>
    private sealed record UpstreamCopy(string Id, string? Identifier);

    /// <returns>
    /// The copies <paramref name="pathAndQuery"/> reads, or null when the upstream could not say:
    /// the read failed (counted against the breaker by <see cref="NightscoutWriteBackSink{T}.GetAsync"/>)
    /// or its body is not an array of documents with a string <c>_id</c> (counted here).
    /// </returns>
    private async Task<IReadOnlyList<UpstreamCopy>?> FindAsync(
        NightscoutConnectorConfiguration config, string pathAndQuery, CancellationToken ct)
    {
        var body = await GetAsync(config, pathAndQuery, ct);
        if (body is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var copies = new List<UpstreamCopy>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object
                        || !element.TryGetProperty("_id", out var id) || id.ValueKind != JsonValueKind.String)
                    {
                        copies = null;
                        break;
                    }

                    copies.Add(new UpstreamCopy(
                        id.GetString()!,
                        element.TryGetProperty("identifier", out var identifier) && identifier.ValueKind == JsonValueKind.String
                            ? identifier.GetString()
                            : null));
                }

                if (copies is not null)
                    return copies;
            }
        }
        catch (JsonException)
        {
        }

        RecordFailure(HttpMethod.Get, null);
        return null;
    }
}
