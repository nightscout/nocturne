using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nocturne.Connectors.Nightscout.Tests.TestSupport;

/// <summary>
/// The treatments collection of a cgm-remote-monitor instance, storing what it is sent as
/// <c>lib/server/treatments.js</c> does at 15.0.8 (<c>UUID_HANDLING</c> on) or at 15.0.6.
/// </summary>
/// <remarks>
/// <para>
/// 15.0.8: a string <c>_id</c> that is not an ObjectId moves to an empty <c>identifier</c> and is
/// dropped, and a 24-hex one becomes an ObjectId. A document carrying an <c>identifier</c> replaces
/// the one matching <c>$or: [{identifier}, {_id: identifier}]</c>, whose <c>_id</c> arm compares a
/// string and so never matches an ObjectId, and keeps that document's <c>_id</c> or mints one; else
/// one with its <c>_id</c>; else by <c>created_at</c> and <c>eventType</c>. POST and PUT upsert alike.
/// </para>
/// <para>
/// 15.0.6: a POST replaces the document matching <c>created_at</c> and <c>eventType</c> with the one
/// sent, <c>_id</c> included as a string, which is an error when that <c>_id</c> differs from the
/// stored one, and inserts it when none matches, which is a duplicate-key error when its <c>_id</c>
/// is taken. A PUT saves under <c>new ObjectID(_id)</c>, which a string <c>_id</c> never equals.
/// </para>
/// Every version keeps <c>identifier</c> as sent and answers <c>find[identifier]</c> within
/// <c>query.js</c>'s default four-day <c>created_at</c> window unless the find bounds
/// <c>created_at</c> itself.
/// </remarks>
internal sealed class FakeNightscoutTreatments(string version) : HttpMessageHandler
{
    private static readonly Regex ObjectIdShape = new("^[0-9a-fA-F]{24}$");
    private int _minted;

    /// <summary>A stored document, with whether its <c>_id</c> is an ObjectId or a string.</summary>
    public sealed record Stored(string Id, bool IsObjectId, JsonObject Document);

    public List<Stored> Documents { get; } = [];

    public int Refusals { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method == HttpMethod.Get)
            return Json(HttpStatusCode.OK, new JsonArray([.. Find(request.RequestUri!).Select(d => d.DeepClone())]));

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
        var docs = body is JsonArray array ? array.Select(d => d!.AsObject()).ToList() : [body.AsObject()];
        foreach (var doc in docs)
        {
            var refused = version == "15.0.8"
                ? Upsert1508(doc)
                : request.Method == HttpMethod.Put ? Save1506(doc) : Upsert1506(doc);
            if (refused)
            {
                Refusals++;
                return Json(HttpStatusCode.InternalServerError, new JsonObject { ["message"] = "Mongo Error" });
            }
        }

        return Json(HttpStatusCode.OK, new JsonArray());
    }

    /// <summary>The clock the default <c>created_at</c> window of a find is taken from.</summary>
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// A <c>find[identifier]</c>, bounded as <c>lib/server/query.js</c> bounds it: a find naming
    /// neither <c>_id</c>, <c>created_at</c> nor <c>dateString</c> only sees <c>created_at</c> in the
    /// last four days (<c>enforceDateFilter</c>, <c>deltaAgo = TWO_DAYS * 2</c>).
    /// </summary>
    private IEnumerable<JsonObject> Find(Uri uri)
    {
        var find = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => (Key: Uri.UnescapeDataString(p[0]), Value: Uri.UnescapeDataString(p.ElementAtOrDefault(1) ?? "")))
            .Where(p => p.Key.StartsWith("find[", StringComparison.Ordinal))
            .ToList();

        var bounds = find.Where(p => p.Key.StartsWith("find[created_at]", StringComparison.Ordinal))
            .Select(p => (Op: p.Key["find[created_at]".Length..], At: DateTimeOffset.Parse(p.Value)))
            .ToList();
        if (bounds.Count == 0 && !find.Any(p => p.Key.StartsWith("find[_id]", StringComparison.Ordinal) || p.Key.StartsWith("find[dateString]", StringComparison.Ordinal)))
            bounds.Add(("[$gte]", Now - TimeSpan.FromDays(4)));

        var identifier = find.Where(p => p.Key == "find[identifier]").Select(p => p.Value).FirstOrDefault();
        return Documents.Select(d => d.Document).Where(d =>
            (identifier is null || (string?)d["identifier"] == identifier)
            && bounds.All(b => InBound(DateTimeOffset.Parse((string)d["created_at"]!), b.Op, b.At)));
    }

    private static bool InBound(DateTimeOffset at, string op, DateTimeOffset bound) => op switch
    {
        "[$gte]" => at >= bound,
        "[$gt]" => at > bound,
        "[$lte]" => at <= bound,
        "[$lt]" => at < bound,
        _ => throw new NotSupportedException($"find[created_at]{op}"),
    };

    /// <summary>Stores <paramref name="doc"/> under a string or ObjectId <c>_id</c>, as a client other than Nocturne would.</summary>
    public void Seed(JsonObject doc, bool asObjectId) =>
        Documents.Add(new Stored((string)doc["_id"]!, asObjectId, doc));

    private bool Upsert1508(JsonObject doc)
    {
        string? id = null;
        var idIsObjectId = false;
        if (doc["_id"]?.GetValue<string>() is { } sentId)
        {
            if (ObjectIdShape.IsMatch(sentId))
                (id, idIsObjectId) = (sentId.ToLowerInvariant(), true);
            else if (doc["identifier"] is null)
                doc["identifier"] = sentId;
            doc.Remove("_id");
        }

        Stored? existing;
        if (doc["identifier"]?.GetValue<string>() is { Length: > 0 } identifier)
        {
            (id, idIsObjectId) = (null, false);
            existing = Documents.FirstOrDefault(d =>
                (string?)d.Document["identifier"] == identifier || (!d.IsObjectId && d.Id == identifier));
        }
        else if (id is not null)
        {
            existing = Documents.FirstOrDefault(d => d.IsObjectId && d.Id == id);
        }
        else
        {
            existing = Documents.FirstOrDefault(d => SameTimeAndType(d.Document, doc));
        }

        Replace(existing, doc, existing?.Id ?? id ?? Mint(), existing?.IsObjectId ?? (id is null || idIsObjectId));
        return false;
    }

    private bool Upsert1506(JsonObject doc)
    {
        var existing = Documents.FirstOrDefault(d => SameTimeAndType(d.Document, doc));
        var sentId = doc["_id"]?.GetValue<string>();
        if (existing is not null)
        {
            if (sentId is not null && (existing.IsObjectId || existing.Id != sentId))
                return true;
            Replace(existing, doc, existing.Id, existing.IsObjectId);
            return false;
        }

        if (sentId is not null && Documents.Any(d => !d.IsObjectId && d.Id == sentId))
            return true;

        Replace(null, doc, sentId ?? Mint(), sentId is null);
        return false;
    }

    private bool Save1506(JsonObject doc)
    {
        var id = doc["_id"]!.GetValue<string>().ToLowerInvariant();
        var existing = Documents.FirstOrDefault(d => d.IsObjectId && d.Id == id);
        Replace(existing, doc, id, true);
        return false;
    }

    private void Replace(Stored? existing, JsonObject doc, string id, bool isObjectId)
    {
        if (existing is not null)
            Documents.Remove(existing);
        doc["_id"] = id;
        Documents.Add(new Stored(id, isObjectId, doc));
    }

    private static bool SameTimeAndType(JsonObject a, JsonObject b) =>
        DateTimeOffset.Parse((string)a["created_at"]!) == DateTimeOffset.Parse((string)b["created_at"]!)
        && (string?)a["eventType"] == (string?)b["eventType"];

    private string Mint() => $"66b0000000000000{_minted++:x8}";

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
