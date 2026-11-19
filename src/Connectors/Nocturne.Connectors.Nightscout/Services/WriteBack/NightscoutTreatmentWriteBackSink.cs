using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Treatments;
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
/// <item>Under any other string <c>_id</c>: a POST under that <c>_id</c>, upserted as above. An
/// edit that moved the time or event type is refused up to 15.0.6 rather than inserted beside it;
/// 15.0.7 and later drop the <c>_id</c> and match the identifier.</item>
/// </list></item>
/// <item>Failing that, under <c>_id</c> = the wire key, as the original of a record a Nightscout
/// migration imported is when its id is an ObjectId. <c>find[_id]</c> names <c>_id</c>, so it skips
/// the four-day window. The edit is PUT under that <c>_id</c>, which every version saves by it, with
/// the identifier the copy holds if it has one: a PUT replaces the whole document, and a v3 client
/// such as AAPS matches its records by it.</item>
/// <item>Failing that, for a legacy id that is not an ObjectId, under <c>_id</c> = that legacy id as
/// a string, as the original of a record a Nightscout migration imported from an uploader that sent
/// its own id is. It is asked as <c>find[_id][$in]</c>, which no version casts to an ObjectId when
/// the value is not 24-hex (15.0.6 casts only a plain <c>find[_id]</c>, and refuses a non-hex one),
/// and which skips the four-day window as well. The edit is POSTed under that <c>_id</c>, with the
/// identifier the original holds, else the legacy id: up to 15.0.6 the POST matches it by
/// <c>created_at</c> and <c>eventType</c> and keeps its <c>_id</c>, and 15.0.7 and later match its
/// string <c>_id</c> through the identifier's <c>$or</c>. The create's <c>_id</c> would be an
/// immutable-field error on every edit up to 15.0.6, and a second copy from 15.0.7.</item>
/// <item>Held nowhere: the create's POST. An edit PUT under its <c>_id</c> alone would be stored by
/// 15.0.7 and later under an ObjectId with no identifier, which a later create's identifier upsert
/// never matches: a second copy.</item>
/// </list>
/// A lookup that cannot say sends nothing and counts against the circuit breaker: either write
/// could store a second copy. The edit is not retried, so upstream keeps the previous version until
/// the treatment is edited again.
/// </para>
/// <para>
/// Once upstream accepts a treatment, the <see cref="WriteBackFingerprint"/> of each document it went
/// as is kept on its rows (<see cref="IWriteBackSentRecords"/>), so the connector's pull tells its
/// echo from an edit made upstream. Before each send they are added to rows that already keep some,
/// since upstream may hold any of them from the moment it is sent: a pull before the stamp, or after a
/// meal's bolus is accepted and its carbs are not, would otherwise apply Nocturne's own document as an
/// upstream edit.
/// </para>
/// </remarks>
public class NightscoutTreatmentWriteBackSink(
    HttpClient httpClient,
    IConnectorConfigurationLoader<NightscoutConnectorConfiguration> configLoader,
    NightscoutCircuitBreaker circuitBreaker,
    IWriteBackSentRecords sentRecords,
    ILogger<NightscoutTreatmentWriteBackSink> logger)
    : NightscoutWriteBackSink<Treatment>(httpClient, configLoader, circuitBreaker, logger)
{
    protected override string Endpoint => "/api/v1/treatments";

    protected override JsonSerializerOptions SerializerOptions => UpstreamIdentityJson.Options;

    protected override bool ShouldSkip(Treatment item)
        => item.DataSource == DataSources.NightscoutConnector;

    protected override async Task<IReadOnlyList<Treatment>> PrepareAsync(
        NightscoutConnectorConfiguration config, IReadOnlyList<Treatment> batch, CancellationToken ct)
    {
        await sentRecords.RecordSendingAsync([.. batch.Select(SentRecord)], ct);
        var sent = new List<Treatment>(batch.Count);
        foreach (var item in batch)
            sent.AddRange(await SplitAsync(config, item, ct));
        return sent;
    }

    protected override async Task AfterWrittenAsync(
        NightscoutConnectorConfiguration config, IReadOnlyList<Treatment> items, CancellationToken ct)
    {
        foreach (var item in items.Where(i => PreBolusSplit.ForWriteBack(i).Count == 1))
            await RemoveSplitCopiesAsync(config, item, ct);

        await sentRecords.RecordSentAsync([.. items.Select(SentRecord)], ct);
    }

    private static WriteBackSentRecord SentRecord(Treatment item) =>
        new(item.LegacyId, item.RecordId, [.. PreBolusSplit.ForWriteBack(item).Select(AsPulled)]);

    /// <summary>
    /// The <see cref="WriteBackFingerprint"/> of <paramref name="document"/> as it is sent and read back:
    /// written with <see cref="UpstreamIdentityJson.Options"/>, which every send uses, and read with
    /// <see cref="UpstreamIdentityJson.ReadOptions"/>, which the connector's pull reads with.
    /// </summary>
    internal static string AsPulled(Treatment document) =>
        WriteBackFingerprint.Of(JsonSerializer.Deserialize<Treatment>(
            JsonSerializer.Serialize(document, UpstreamIdentityJson.Options), UpstreamIdentityJson.ReadOptions)!);

    /// <summary>
    /// Up to 15.0.6 the POST of the carbs record a v1 POST split off a meal upserts by time onto the
    /// carbs write-back split off the meal when it was one record, and is refused for the <c>_id</c>
    /// it would change. That document is removed, as the update of the meal's bolus that follows would
    /// remove it. From 15.0.7 the POST is matched by its identifier and is not refused. Any other
    /// refusal clears nothing.
    /// </summary>
    protected override async Task<bool> ClearRefusalAsync(
        NightscoutConnectorConfiguration config, IReadOnlyList<Treatment> batch, string refusal, CancellationToken ct)
    {
        if (!IsImmutableIdRefusal(refusal))
            return false;

        var cleared = false;
        foreach (var mealId in batch.Select(t => t.PreBolusMealId).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var carbsForms = WriteBackCarbsForms(mealId);
            if (await FindAsync(config, IdentifierQuery($"{Endpoint}.json", carbsForms), ct) is { Count: > 0 })
                cleared |= await RemoveAsync(config, carbsForms, ct);
        }

        return cleared;
    }

    /// <summary>MongoDB's ImmutableField error: an update that would change a stored document's <c>_id</c>.</summary>
    private const int ImmutableFieldCode = 66;

    /// <summary>
    /// Whether a refused POST carries <see cref="ImmutableFieldCode"/>. 15.0.6 answers
    /// <c>{status: 500, message: 'Mongo Error', description: err}</c>, <c>err</c> being the driver's
    /// error, or an array of one per refused document for an array POST. Serialized, a driver error
    /// keeps only <c>driver</c>, <c>name</c>, <c>index</c> and <c>code</c>, so the code is all there is
    /// to match.
    /// </summary>
    private static bool IsImmutableIdRefusal(string refusal)
    {
        try
        {
            using var document = JsonDocument.Parse(refusal);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("description", out var description))
                return false;

            return description.ValueKind == JsonValueKind.Array
                ? description.EnumerateArray().Any(HasImmutableFieldCode)
                : HasImmutableFieldCode(description);
        }
        catch (JsonException)
        {
            return false;
        }

        static bool HasImmutableFieldCode(JsonElement error) =>
            error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var value)
            && value == ImmutableFieldCode;
    }

    private static IReadOnlyList<string> WriteBackCarbsForms(string legacyId) =>
        UpstreamIdentityJson.TreatmentWireForms(new Treatment { Id = PreBolusSplit.WriteBackCarbsId(legacyId) });

    /// <summary>
    /// <see cref="PreBolusSplit.ForWriteBack"/> of <paramref name="item"/>. The split-off carbs go to
    /// the slot an earlier write-back's split copy (<see cref="PreBolusSplitCopy"/>) holds, and up to
    /// 15.0.6 a POST there upserts onto that copy, refused for the <c>_id</c> it would change, so the
    /// copy is removed first. If the carbs then fail to go, upstream lacks them until the next edit.
    /// </summary>
    private async Task<IReadOnlyList<Treatment>> SplitAsync(
        NightscoutConnectorConfiguration config, Treatment item, CancellationToken ct)
    {
        var halves = PreBolusSplit.ForWriteBack(item);
        if (halves.Count == 2)
            await RemoveSplitCopiesAsync(config, item, ct);
        return halves;
    }

    private async Task RemoveSplitCopiesAsync(NightscoutConnectorConfiguration config, Treatment item, CancellationToken ct)
    {
        if (PreBolusSplitCopy.CreatedAt(item) is not { } createdAt)
            return;

        var found = await ExchangeAsync(config, HttpMethod.Get, PreBolusSplitCopy.Query(Endpoint, item, createdAt), ct);
        if (found is null)
            return;

        foreach (var id in PreBolusSplitCopy.Ids(found, item, createdAt))
            await ExchangeAsync(config, HttpMethod.Delete, $"{Endpoint}/{Uri.EscapeDataString(id)}", ct);
    }

    /// <remarks>
    /// The identifier lookup also asks for the carbs an earlier write-back split off the meal
    /// (<see cref="PreBolusSplit.WriteBackCarbsId"/> of its legacy id), which only write-back sends.
    /// Once the bolus is accepted, an edit that stops the split removes them, and one that keeps it
    /// writes them in place as any edit is. Up to 15.0.6 a copy held under its identifier as its
    /// <c>_id</c> is written by a POST that upserts by time, which a moved time turns into a refused
    /// insert, so such a copy is removed and its carbs created again.
    /// </remarks>
    protected override async Task<bool> SendUpdatedAsync(
        NightscoutConnectorConfiguration config, Treatment item, CancellationToken ct)
    {
        await sentRecords.RecordSendingAsync([SentRecord(item)], ct);
        var halves = await SplitAsync(config, item, ct);
        if (item.LegacyId is not { Length: > 0 } legacyId)
            return await SendEditAsync(config, halves[0], null, ct);

        var carbsForms = WriteBackCarbsForms(legacyId);
        var forms = UpstreamIdentityJson.TreatmentWireForms(halves[0]);
        if (await FindAsync(config, IdentifierQuery($"{Endpoint}.json", [.. forms, .. carbsForms]), ct) is not { } found
            || !await SendEditAsync(config, halves[0], found, ct))
            return false;

        var carbsCopy = Preferred(found, carbsForms);
        if (halves.Count == 1)
            return carbsCopy is null || await RemoveAsync(config, carbsForms, ct);

        if (carbsCopy is { } held && held.Id == held.Identifier && Mills(held.CreatedAt) != halves[1].Mills)
        {
            if (!await RemoveAsync(config, carbsForms, ct))
                return false;
            found = [];
        }

        return await SendEditAsync(config, halves[1], found, ct);
    }

    /// <summary>
    /// Removes the copies held under any of <paramref name="identifiers"/>, by a query: up to 15.0.6
    /// one may be held under a string <c>_id</c>, which <c>DELETE /treatments/:id</c> casts to an ObjectId.
    /// </summary>
    private async Task<bool> RemoveAsync(NightscoutConnectorConfiguration config, IReadOnlyList<string> identifiers, CancellationToken ct)
        => await ExchangeAsync(config, HttpMethod.Delete, IdentifierQuery(Endpoint, identifiers), ct) is not null;

    private static long? Mills(string? createdAt) =>
        DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUnixTimeMilliseconds()
            : null;

    /// <param name="byIdentifier">What a lookup under the item's identifier forms found, or null to look.</param>
    private async Task<bool> SendEditAsync(
        NightscoutConnectorConfiguration config, Treatment item, IReadOnlyList<UpstreamCopy>? byIdentifier, CancellationToken ct)
    {
        if (UpstreamIdentityJson.TreatmentWireKey(item) is not { } key)
            return await SendAsync(config, HttpMethod.Post, new[] { item }, SerializerOptions, ct);

        var forms = UpstreamIdentityJson.TreatmentWireForms(item);
        if ((byIdentifier ?? await FindAsync(config, IdentifierQuery($"{Endpoint}.json", forms), ct)) is not { } copies)
            return false;

        if (Preferred(copies, forms) is { } copy)
        {
            var identifier = copy.Identifier!;
            // A read serves an ObjectId as lowercase hex, so any other _id is a string.
            if (!MongoObjectId.IsObjectId(copy.Id))
                return await PostAsync(config, item, copy.Id, identifier, ct);

            if (copy.Id == identifier)
            {
                if (await FindAsync(config, IdQuery(copy.Id), ct) is not { } underObjectId)
                    return false;
                if (underObjectId.Count == 0)
                    return await PostAsync(config, item, identifier, identifier, ct);
            }

            return await PutAsync(config, item, copy.Id, identifier, ct);
        }

        if (await FindAsync(config, IdQuery(key), ct) is not { } byId)
            return false;
        if (byId.FirstOrDefault() is { } original)
            return await PutAsync(config, item, key, original.Identifier, ct);

        if (item.LegacyId is { Length: > 0 } legacyId && !MongoObjectId.IsObjectId(legacyId))
        {
            if (await FindAsync(config, StringIdQuery(legacyId), ct) is not { } byStringId)
                return false;
            if (byStringId.FirstOrDefault(c => c.Id == legacyId) is { } imported)
                return await PostAsync(config, item, legacyId, imported.Identifier ?? legacyId, ct);
        }

        return await SendCreateAsync(config, new[] { UpstreamIdentityJson.TreatmentPayload(item, key, key) }, [item], null, ct);
    }

    private Task<bool> PostAsync(NightscoutConnectorConfiguration config, Treatment item, string id, string identifier, CancellationToken ct)
        => SendAsync(config, HttpMethod.Post, new[] { UpstreamIdentityJson.TreatmentPayload(item, id, identifier) }, null, ct);

    private Task<bool> PutAsync(NightscoutConnectorConfiguration config, Treatment item, string id, string? identifier, CancellationToken ct)
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

    private static string IdentifierQuery(string path, IReadOnlyList<string> forms)
    {
        var query = new StringBuilder($"{path}?");
        for (var i = 0; i < forms.Count; i++)
            query.Append($"find[identifier][$in][{i}]={Uri.EscapeDataString(forms[i])}&");
        return query.Append($"find[created_at][$gte]={Uri.EscapeDataString(LookupCreatedAtFloor)}&count={LookupCount}").ToString();
    }

    private string IdQuery(string objectId) => $"{Endpoint}.json?find[_id]={Uri.EscapeDataString(objectId)}&count=1";

    private string StringIdQuery(string id) => $"{Endpoint}.json?find[_id][$in][0]={Uri.EscapeDataString(id)}&count=1";

    /// <summary>The copy held under the earliest of <paramref name="forms"/> any copy is held under.</summary>
    private static UpstreamCopy? Preferred(IReadOnlyList<UpstreamCopy> copies, IReadOnlyList<string> forms) =>
        forms.Select(form => copies.FirstOrDefault(c => c.Identifier == form)).FirstOrDefault(c => c is not null);

    /// <summary>A copy upstream: the <c>_id</c> the read serves it by, its <c>identifier</c> and <c>created_at</c>.</summary>
    private sealed record UpstreamCopy(string Id, string? Identifier, string? CreatedAt);

    /// <returns>
    /// The copies <paramref name="pathAndQuery"/> reads, or null when the upstream could not say:
    /// the read failed (counted against the breaker by <see cref="NightscoutWriteBackSink{T}.ExchangeAsync"/>)
    /// or its body is not an array of documents with a string <c>_id</c> (counted here).
    /// </returns>
    private async Task<IReadOnlyList<UpstreamCopy>?> FindAsync(
        NightscoutConnectorConfiguration config, string pathAndQuery, CancellationToken ct)
    {
        if (await ExchangeAsync(config, HttpMethod.Get, pathAndQuery, ct) is not { } body)
            return null;

        if (ParseCopies(body) is { } copies)
            return copies;

        RecordFailure(null, HttpMethod.Get);
        return null;
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<UpstreamCopy>? ParseCopies(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var copies = new List<UpstreamCopy>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("_id", out var id) || id.ValueKind != JsonValueKind.String)
                    return null;

                copies.Add(new UpstreamCopy(id.GetString()!, StringOf(element, "identifier"), StringOf(element, "created_at")));
            }

            return copies;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
