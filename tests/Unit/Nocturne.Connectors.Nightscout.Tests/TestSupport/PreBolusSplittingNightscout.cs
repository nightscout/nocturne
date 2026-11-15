using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nocturne.Connectors.Nightscout.Tests.TestSupport;

/// <summary>
/// The v1 treatment endpoints of Nightscout's <c>lib/server/treatments.js</c> and
/// <c>lib/api/treatments/index.js</c> as far as <c>preBolus</c> reaches them, at 15.0.6 (upsert by
/// <c>created_at</c> and <c>eventType</c>, <c>_id</c> kept as sent; PUT saves by <c>_id</c>) or 15.0.8
/// (upsert and PUT by <c>identifier</c>, else <c>_id</c>, else <c>created_at</c> and
/// <c>eventType</c>; a non-ObjectId <c>_id</c> moves to an empty <c>identifier</c>).
/// </summary>
/// <remarks>
/// <c>prepareData</c> moves the carbs of a document with a non-zero <c>preBolus</c> into
/// <c>preBolusCarbs</c> and deletes them. A 15.0.6 POST whose upsert would change a stored document's
/// <c>_id</c>, or insert one a document already holds, is refused, as MongoDB refuses it. A POST, an
/// array one document at a time, then upserts a
/// second document <c>{created_at: created_at + preBolus minutes, eventType, carbs: preBolusCarbs,
/// notes}</c> by <c>created_at</c> and <c>eventType</c> under a minted <c>_id</c>, carbs or not. A PUT
/// writes no second document. A read serves <c>carbs</c> and <c>insulin</c> through
/// <c>Number()</c>. Shared with the API tests, which pull back what it stores.
/// <para>
/// A 15.0.6 POST keeps the <c>_id</c> it is sent as a string; every other <c>_id</c> is an ObjectId.
/// <c>lib/server/query.js</c> casts a plain <c>find[_id]</c>, and so the <c>_id</c> of
/// <c>DELETE /treatments/:id</c>, to an ObjectId, which matches no string; <c>find[_id][$in]</c> is not
/// cast, and matches only a string. <c>DELETE /treatments/</c> removes what its query finds.
/// </para>
/// </remarks>
internal sealed class PreBolusSplittingNightscout(bool v15_0_8 = true) : HttpMessageHandler
{
    private const string Path = "/api/v1/treatments";
    private int _minted;
    private readonly HashSet<JsonObject> _stringIds = new(ReferenceEqualityComparer.Instance);

    public List<JsonObject> Documents { get; } = [];

    /// <summary>Every document as a read serves it.</summary>
    public string Served() => Serve(Documents);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (request.Method == HttpMethod.Get && uri.AbsolutePath == Path + ".json")
            return Json(Serve(Find(Uri.UnescapeDataString(uri.Query))));

        if (request.Method == HttpMethod.Delete && uri.AbsolutePath.TrimEnd('/') == Path)
            return Removed(Find(Uri.UnescapeDataString(uri.Query)).ToList());

        if (request.Method == HttpMethod.Delete && uri.AbsolutePath.StartsWith(Path + "/", StringComparison.Ordinal))
        {
            var id = uri.AbsolutePath[(Path.Length + 1)..];
            return Removed(Documents.Where(d => Id(d) == id && !_stringIds.Contains(d)).ToList());
        }

        if (uri.AbsolutePath != Path)
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
        if (request.Method == HttpMethod.Post)
        {
            if (!Post(body is JsonArray array ? array : new JsonArray(body.DeepClone())))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
        else if (request.Method == HttpMethod.Put)
            Save(body.DeepClone().AsObject());
        else
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

        return Json("[]");
    }

    private HttpResponseMessage Removed(List<JsonObject> removed)
    {
        Documents.RemoveAll(removed.Contains);
        return Json($$"""{"n":{{removed.Count}}}""");
    }

    /// <returns>Whether every document was stored.</returns>
    public bool Post(JsonArray documents)
    {
        var stored = true;
        foreach (var node in documents)
        {
            var obj = node!.DeepClone().AsObject();
            NormalizeId(obj);
            var (createdAt, preBolusCarbs) = PrepareData(obj);
            var eventType = obj["eventType"]?.GetValue<string>();
            stored &= Upsert(v15_0_8 ? null : (createdAt, eventType), obj, createdAt);

            if (obj["preBolus"] is { } preBolus)
            {
                var pbCreatedAt = Iso(DateTimeOffset.Parse(createdAt, CultureInfo.InvariantCulture)
                    .AddMinutes(preBolus.GetValue<double>()));
                var pbTreat = new JsonObject
                {
                    ["created_at"] = pbCreatedAt,
                    ["eventType"] = eventType,
                    ["carbs"] = preBolusCarbs?.DeepClone() ?? JsonValue.Create(""),
                };
                if (obj["notes"] is { } notes)
                    pbTreat["notes"] = notes.DeepClone();
                Upsert((pbCreatedAt, eventType), pbTreat, pbCreatedAt);
            }
        }

        return stored;
    }

    private void Save(JsonObject obj)
    {
        NormalizeId(obj);
        var (createdAt, _) = PrepareData(obj);
        if (v15_0_8)
        {
            Upsert(null, obj, createdAt);
            return;
        }

        obj["_id"] ??= Mint();
        var stored = Documents.FindIndex(d => Id(d) == Id(obj) && !_stringIds.Contains(d));
        if (stored >= 0)
            Documents[stored] = obj;
        else
            Documents.Add(obj);
    }

    /// <param name="timeAndType">The key a 15.0.6 POST and every preBolus copy upsert by.</param>
    private bool Upsert((string CreatedAt, string? EventType)? timeAndType, JsonObject obj, string createdAt)
    {
        Predicate<JsonObject> match;
        if (timeAndType is { } key)
        {
            match = d => Str(d, "created_at") == key.CreatedAt && Str(d, "eventType") == key.EventType;
        }
        else if (Str(obj, "identifier") is { } identifier)
        {
            obj.Remove("_id");
            match = d => Str(d, "identifier") == identifier || Id(d) == identifier;
        }
        else if (Id(obj) is { } id)
        {
            match = d => Id(d) == id;
        }
        else
        {
            var eventType = Str(obj, "eventType");
            match = d => Str(d, "created_at") == createdAt && Str(d, "eventType") == eventType;
        }

        var stored = Documents.FindIndex(match);
        if (stored >= 0)
        {
            if (timeAndType is not null && Id(obj) is { } sent && sent != Id(Documents[stored]))
                return false;
            obj["_id"] = Documents[stored]["_id"]!.DeepClone();
            if (_stringIds.Remove(Documents[stored]))
                _stringIds.Add(obj);
            Documents[stored] = obj;
            return true;
        }

        if (Id(obj) is { } held && Documents.Any(d => Id(d) == held))
            return false;
        if (timeAndType is not null && Id(obj) is not null && !v15_0_8)
            _stringIds.Add(obj);
        obj["_id"] ??= Mint();
        Documents.Add(obj);
        return true;
    }

    private void NormalizeId(JsonObject obj)
    {
        if (!v15_0_8 || Id(obj) is not { } id || IsObjectId(id))
            return;

        if (Str(obj, "identifier") is null)
            obj["identifier"] = id;
        obj.Remove("_id");
    }

    private static (string CreatedAt, JsonNode? PreBolusCarbs) PrepareData(JsonObject obj)
    {
        var createdAt = Iso(DateTimeOffset.Parse(obj["created_at"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        if (Str(obj, "eventTime") is { } eventTime)
            createdAt = Iso(DateTimeOffset.Parse(eventTime, CultureInfo.InvariantCulture));
        obj["created_at"] = createdAt;
        obj.Remove("eventTime");

        var preBolus = Number(obj["preBolus"]);
        var carbs = Number(obj["carbs"]);
        JsonNode? preBolusCarbs = null;
        if (!IsEmpty(preBolus) && !IsEmpty(carbs))
        {
            preBolusCarbs = JsonValue.Create(carbs);
            obj.Remove("carbs");
        }

        foreach (var (field, value) in new[] { ("carbs", carbs), ("insulin", Number(obj["insulin"])), ("preBolus", preBolus) })
        {
            if (IsEmpty(value))
                obj.Remove(field);
            else if (obj.ContainsKey(field))
                obj[field] = value;
        }

        return (createdAt, preBolusCarbs);
    }

    private IEnumerable<JsonObject> Find(string query)
    {
        var finds = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].StartsWith("find[", StringComparison.Ordinal))
            .ToList();

        var identifiers = InValues("identifier");
        var ids = InValues("_id");
        return Documents.Where(d =>
            (identifiers.Count == 0 || identifiers.Contains(Str(d, "identifier")))
            && (ids.Count == 0 || (ids.Contains(Id(d)) && _stringIds.Contains(d)))
            && finds.Where(f => !f[0].Contains("[$in]", StringComparison.Ordinal)).All(f => f[0] switch
            {
                "find[created_at][$gte]" => string.CompareOrdinal(Str(d, "created_at"), f[1]) >= 0,
                "find[created_at][$lte]" => string.CompareOrdinal(Str(d, "created_at"), f[1]) <= 0,
                "find[eventType]" => Str(d, "eventType") == f[1],
                "find[_id]" => Id(d) == f[1] && !_stringIds.Contains(d),
                _ => throw new NotSupportedException(f[0]),
            }));

        List<string?> InValues(string field) =>
            [.. finds.Where(f => f[0].StartsWith($"find[{field}][$in][", StringComparison.Ordinal)).Select(f => (string?)f[1])];
    }

    private static string Serve(IEnumerable<JsonObject> documents)
    {
        var served = new JsonArray();
        foreach (var document in documents)
        {
            var copy = document.DeepClone().AsObject();
            copy["carbs"] = Served(Number(copy["carbs"]));
            copy["insulin"] = Served(Number(copy["insulin"]));
            served.Add(copy);
        }

        return served.ToJsonString();
    }

    private static JsonNode? Served(double value) => double.IsNaN(value) ? null : JsonValue.Create(value);

    /// <summary>JavaScript's <c>Number()</c>: a missing field is NaN, null and "" are 0.</summary>
    private static double Number(JsonNode? node) => node is null ? double.NaN : node.GetValueKind() switch
    {
        JsonValueKind.Number => node.GetValue<double>(),
        JsonValueKind.String when node.GetValue<string>().Trim().Length == 0 => 0,
        JsonValueKind.String => double.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.NaN,
        _ => double.NaN,
    };

    private static bool IsEmpty(double value) => value == 0 || double.IsNaN(value);

    private static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string? Id(JsonObject obj) => Str(obj, "_id");

    private static bool IsObjectId(string id) => id.Length == 24 && id.All(Uri.IsHexDigit);

    private static string Iso(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private string Mint() => $"{++_minted:x24}";

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
