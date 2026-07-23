using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace Nocturne.Core.Models.Queries;

/// <summary>
/// Parsed representation of a Nightscout-style MongoDB <c>find</c> query, accepted in both wire
/// forms: querystring (<c>find[eventType][$ne]=Note</c>, including <c>find[$and][0][...]</c> /
/// <c>find[$or][0][...]</c> groups) and JSON (<c>{"created_at":{"$gte":"2023-01-01"}}</c>).
/// Supports $eq, $ne, $gt, $gte, $lt, $lte, $in, $nin, $regex (with $options), $exists, $and, $or.
/// </summary>
/// <remarks>
/// Time bounds on recognised timestamp fields (<c>date</c>, <c>mills</c>, <c>created_at</c>,
/// <c>dateString</c>, <c>timestamp</c>) are extracted into <see cref="FromMills"/> /
/// <see cref="ToMills"/> so callers can push the range down to the database. All remaining
/// conditions must be applied per document via <see cref="Matches{T}(T)"/>, which evaluates the
/// query against the document's serialized wire shape — the same JSON legacy clients see — so
/// field names and value semantics match legacy Nightscout 1:1.
/// </remarks>
public sealed class FindQuery
{
    /// <summary>Fields whose range constraints are extracted as time bounds for pushdown.</summary>
    private static readonly HashSet<string> TimeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "mills", "created_at", "dateString", "timestamp",
    };

    /// <summary>
    /// Wire-shape serializer options: camelCase like ASP.NET's defaults, with the models'
    /// <c>[JsonPropertyName]</c> attributes taking precedence.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>An empty query: no time bounds, no field filters, matches everything.</summary>
    public static readonly FindQuery Empty = new(new Group(IsAnd: true, []), null, null);

    private readonly Group _root;

    private FindQuery(Group root, long? fromMills, long? toMills)
    {
        _root = root;
        FromMills = fromMills;
        ToMills = toMills;
    }

    /// <summary>Inclusive lower time bound in Unix milliseconds, if the query constrains one.</summary>
    public long? FromMills { get; }

    /// <summary>Inclusive upper time bound in Unix milliseconds, if the query constrains one.</summary>
    public long? ToMills { get; }

    /// <summary><c>true</c> when the query contains no conditions at all.</summary>
    public bool IsEmpty => _root.Children.Count == 0;

    /// <summary>
    /// <c>true</c> when the query contains conditions beyond the extracted
    /// <see cref="FromMills"/>/<see cref="ToMills"/> bounds, so results fetched by time range
    /// alone must additionally be filtered through <see cref="Matches{T}(T)"/>.
    /// </summary>
    public bool HasFieldFilters => HasFieldFiltersExcept(null);

    /// <summary>
    /// Like <see cref="HasFieldFilters"/> but ignoring a simple top-level equality on
    /// <paramref name="exceptField"/> (which the caller handles separately, e.g. entry
    /// <c>type</c> routing).
    /// </summary>
    public bool HasFieldFiltersExcept(string? exceptField)
    {
        return CountResidual(_root, andReachable: true, exceptField) > 0;
    }

    /// <summary>
    /// Returns the value of a top-level (AND-reachable) <c>$eq</c> condition on
    /// <paramref name="field"/>, or <c>null</c> when the query doesn't constrain the field to a
    /// single equality.
    /// </summary>
    public string? GetEqualityValue(string field)
    {
        string? found = null;
        foreach (var cond in EnumerateAndReachable(_root))
        {
            if (!cond.Path.Equals(field, StringComparison.OrdinalIgnoreCase))
                continue;
            if (cond.Op != "$eq")
                return null;
            if (found != null && found != cond.StringValue)
                return null;
            found = cond.StringValue;
        }
        return found;
    }

    /// <summary>
    /// Parses a find query from either wire form. A null/empty/unparseable input yields
    /// <see cref="Empty"/> (no filtering), matching the legacy server's lenient behavior.
    /// </summary>
    public static FindQuery Parse(string? find)
    {
        if (string.IsNullOrWhiteSpace(find) || find.Trim() == "{}")
            return Empty;

        try
        {
            Group root;
            if (find.Contains("find[", StringComparison.OrdinalIgnoreCase)
                || find.Contains("find%5B", StringComparison.OrdinalIgnoreCase))
            {
                root = ParseQueryString(find);
            }
            else if (find.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(find);
                root = ParseJsonObject(doc.RootElement, path: null, depth: 0);
            }
            else
            {
                return Empty;
            }

            var (from, to) = ExtractTimeBounds(root);
            return new FindQuery(root, from, to);
        }
        catch (Exception)
        {
            // Malformed queries never break a read; legacy Nightscout silently ignores them.
            return Empty;
        }
    }

    /// <summary>
    /// Evaluates the query against a document's serialized wire shape.
    /// </summary>
    public bool Matches<T>(T document)
    {
        return Matches(JsonSerializer.SerializeToElement(document, WireOptions));
    }

    /// <summary>
    /// Evaluates the query against a JSON document with MongoDB semantics: <c>$ne</c>,
    /// <c>$nin</c>, and <c>$exists:false</c> match documents where the field is absent or null;
    /// all other operators require a present, non-null field.
    /// </summary>
    public bool Matches(JsonElement document)
    {
        return EvaluateGroup(_root, document);
    }

    #region Condition tree

    private sealed record Group(bool IsAnd, List<object> Children);

    /// <summary>
    /// A single field condition. <see cref="StringValue"/> carries the querystring-form operand;
    /// <see cref="JsonValue"/> the JSON-form operand (needed for typed arrays under $in/$nin).
    /// </summary>
    private sealed record Condition(string Path, string Op, string StringValue, JsonElement? JsonValue, string? RegexOptions);

    #endregion

    #region Parsing — querystring form

    private static Group ParseQueryString(string queryString)
    {
        var parsed = HttpUtility.ParseQueryString(queryString);

        // First pass: flatten find[...] keys into (groupKey, field, op, value) entries.
        // groupKey is "" for top-level conditions, "and:0"/"or:1" for logical group clauses.
        var entries = new List<(string GroupKey, string Field, string Op, string Value)>();
        foreach (string? key in parsed.AllKeys)
        {
            if (key is null || !key.StartsWith("find[", StringComparison.OrdinalIgnoreCase))
                continue;

            var values = parsed.GetValues(key);
            if (values is null)
                continue;

            // Logical group entries: find[$and][0][field][$op] / find[$or][0][field][$op]
            var logical = Regex.Match(key, @"^find\[\$(and|or)\]\[(\d+)\]\[([^\]]+)\](?:\[(\$\w+)\])?$");
            if (logical.Success)
            {
                var groupKey = $"{logical.Groups[1].Value}:{logical.Groups[2].Value}";
                var op = logical.Groups[4].Success ? logical.Groups[4].Value : "$eq";
                foreach (var value in values)
                    entries.Add((groupKey, logical.Groups[3].Value, op, value));
                continue;
            }

            // Plain field entries: find[field] / find[field][$op] / find[field][$in][]
            var plain = Regex.Match(key, @"^find\[([^\]]+)\](?:\[(\$\w+)\])?(?:\[\])?$");
            if (plain.Success)
            {
                var op = plain.Groups[2].Success ? plain.Groups[2].Value : "$eq";
                foreach (var value in values)
                    entries.Add(("", plain.Groups[1].Value, op, value));
            }
        }

        // $options entries modify the sibling $regex on the same field rather than standing alone
        var regexOptions = entries
            .Where(e => e.Op == "$options")
            .ToDictionary(e => (e.GroupKey, Field: e.Field.ToLowerInvariant()), e => e.Value);

        // Second pass: build the tree. Clauses sharing a group index are AND'd internally;
        // "or:*" clauses combine under a single OR group, "and:*" clauses join the root AND.
        var root = new Group(IsAnd: true, []);
        var clauses = new Dictionary<string, Group>();
        Group? orGroup = null;

        foreach (var (groupKey, field, op, value) in entries)
        {
            if (op == "$options")
                continue;

            var owner = root;
            if (groupKey.Length > 0)
            {
                if (!clauses.TryGetValue(groupKey, out var clause))
                {
                    clause = new Group(IsAnd: true, []);
                    clauses[groupKey] = clause;
                    if (groupKey.StartsWith("or:", StringComparison.Ordinal))
                    {
                        orGroup ??= AddChildGroup(root, isAnd: false);
                        orGroup.Children.Add(clause);
                    }
                    else
                    {
                        root.Children.Add(clause);
                    }
                }
                owner = clause;
            }

            var options = op == "$regex"
                ? regexOptions.GetValueOrDefault((groupKey, field.ToLowerInvariant()))
                : null;
            owner.Children.Add(new Condition(field, op, value, JsonValue: null, RegexOptions: options));
        }

        return root;
    }

    private static Group AddChildGroup(Group parent, bool isAnd)
    {
        var child = new Group(isAnd, []);
        parent.Children.Add(child);
        return child;
    }

    #endregion

    #region Parsing — JSON form

    private const int MaxDepth = 10;

    private static Group ParseJsonObject(JsonElement element, string? path, int depth)
    {
        if (depth > MaxDepth)
            return new Group(IsAnd: true, []);

        var group = new Group(IsAnd: true, []);
        string? pendingRegexOptions = null;
        Condition? pendingRegex = null;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "$and":
                case "$or":
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        var logical = new Group(IsAnd: property.Name == "$and", []);
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object)
                                logical.Children.Add(ParseJsonObject(item, path: null, depth + 1));
                        }
                        group.Children.Add(logical);
                    }
                    break;

                case "$options" when path != null:
                    pendingRegexOptions = property.Value.GetString();
                    break;

                case "$eq" or "$ne" or "$gt" or "$gte" or "$lt" or "$lte"
                    or "$in" or "$nin" or "$regex" or "$exists" when path != null:
                    var cond = new Condition(
                        path, property.Name, JsonScalarToString(property.Value),
                        property.Value.Clone(), RegexOptions: null);
                    if (property.Name == "$regex")
                        pendingRegex = cond;
                    group.Children.Add(cond);
                    break;

                default:
                    var childPath = path is null ? property.Name : $"{path}.{property.Name}";
                    switch (property.Value.ValueKind)
                    {
                        case JsonValueKind.Object:
                            group.Children.Add(ParseJsonObject(property.Value, childPath, depth + 1));
                            break;
                        case JsonValueKind.Array:
                            group.Children.Add(new Condition(
                                childPath, "$in", JsonScalarToString(property.Value),
                                property.Value.Clone(), RegexOptions: null));
                            break;
                        default:
                            group.Children.Add(new Condition(
                                childPath, "$eq", JsonScalarToString(property.Value),
                                property.Value.Clone(), RegexOptions: null));
                            break;
                    }
                    break;
            }
        }

        if (pendingRegex != null && pendingRegexOptions != null)
        {
            var index = group.Children.IndexOf(pendingRegex);
            if (index >= 0)
                group.Children[index] = pendingRegex with { RegexOptions = pendingRegexOptions };
        }

        return group;
    }

    private static string JsonScalarToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => element.GetRawText(),
        };
    }

    #endregion

    #region Time bound extraction

    private static (long? From, long? To) ExtractTimeBounds(Group root)
    {
        long? from = null;
        long? to = null;

        foreach (var cond in EnumerateAndReachable(root))
        {
            if (!TimeFields.Contains(cond.Path))
                continue;
            if (!TryConvertToMills(cond, out var mills))
                continue;

            switch (cond.Op)
            {
                // Strict bounds tighten by 1ms so the inclusive repository window stays exact.
                case "$gte":
                    from = Max(from, mills);
                    break;
                case "$gt":
                    from = Max(from, mills + 1);
                    break;
                case "$lte":
                    to = Min(to, mills);
                    break;
                case "$lt":
                    to = Min(to, mills - 1);
                    break;
                case "$eq":
                    from = Max(from, mills);
                    to = Min(to, mills);
                    break;
            }
        }

        return (from, to);
    }

    private static long? Max(long? current, long candidate)
        => current.HasValue ? Math.Max(current.Value, candidate) : candidate;

    private static long? Min(long? current, long candidate)
        => current.HasValue ? Math.Min(current.Value, candidate) : candidate;

    /// <summary>
    /// Enumerates conditions reachable through AND-only paths from the root — the ones that must
    /// hold for every matching document, and therefore the only ones safe to push down.
    /// </summary>
    private static IEnumerable<Condition> EnumerateAndReachable(Group group)
    {
        if (!group.IsAnd)
            yield break;

        foreach (var child in group.Children)
        {
            switch (child)
            {
                case Condition cond:
                    yield return cond;
                    break;
                case Group nested when nested.IsAnd:
                    foreach (var cond in EnumerateAndReachable(nested))
                        yield return cond;
                    break;
            }
        }
    }

    /// <summary>
    /// Counts conditions not captured by the extracted time bounds (everything inside $or, every
    /// non-time field, and non-range operators on time fields).
    /// </summary>
    private static int CountResidual(Group group, bool andReachable, string? exceptField)
    {
        var count = 0;
        foreach (var child in group.Children)
        {
            switch (child)
            {
                case Condition cond:
                    var isCapturedTimeBound = andReachable
                        && TimeFields.Contains(cond.Path)
                        && cond.Op is "$eq" or "$gt" or "$gte" or "$lt" or "$lte"
                        && TryConvertToMills(cond, out _);
                    var isExceptedEquality = exceptField != null
                        && cond.Op == "$eq"
                        && andReachable
                        && cond.Path.Equals(exceptField, StringComparison.OrdinalIgnoreCase);
                    if (!isCapturedTimeBound && !isExceptedEquality)
                        count++;
                    break;
                case Group nested:
                    count += CountResidual(nested, andReachable && nested.IsAnd, exceptField);
                    break;
            }
        }
        return count;
    }

    private static bool TryConvertToMills(Condition cond, out long mills)
    {
        mills = 0;

        // Numeric operand: epoch milliseconds (the wire form for date/mills)
        if (long.TryParse(cond.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            mills = numeric;
            return true;
        }

        // ISO 8601 operand (created_at / dateString)
        if (DateTimeOffset.TryParse(
                cond.StringValue.Trim('\'', '"'), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            mills = parsed.ToUnixTimeMilliseconds();
            return true;
        }

        return false;
    }

    #endregion

    #region Evaluation

    private static bool EvaluateGroup(Group group, JsonElement document)
    {
        if (group.Children.Count == 0)
            return true;

        foreach (var child in group.Children)
        {
            var matched = child switch
            {
                Condition cond => EvaluateCondition(cond, document),
                Group nested => EvaluateGroup(nested, document),
                _ => true,
            };

            if (group.IsAnd && !matched)
                return false;
            if (!group.IsAnd && matched)
                return true;
        }

        return group.IsAnd;
    }

    private static bool EvaluateCondition(Condition cond, JsonElement document)
    {
        var field = ResolveField(document, cond.Path);
        var missing = field is null || field.Value.ValueKind == JsonValueKind.Null;

        switch (cond.Op)
        {
            case "$exists":
                var wantExists = cond.StringValue.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || cond.StringValue == "1";
                return wantExists != missing;

            // Mongo semantics: negative operators match documents missing the field
            case "$ne":
                return missing || !EqualsOperand(field!.Value, cond);
            case "$nin":
                return missing || !InOperand(field!.Value, cond);
        }

        if (missing)
            return false;

        return cond.Op switch
        {
            "$eq" => EqualsOperand(field!.Value, cond),
            "$in" => InOperand(field!.Value, cond),
            "$gt" => Compare(field!.Value, cond) is > 0,
            "$gte" => Compare(field!.Value, cond) is >= 0,
            "$lt" => Compare(field!.Value, cond) is < 0,
            "$lte" => Compare(field!.Value, cond) is <= 0,
            "$regex" => RegexMatches(field!.Value, cond),
            _ => false,
        };
    }

    private static JsonElement? ResolveField(JsonElement document, string path)
    {
        var current = document;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(part, out var next))
                return null;
            current = next;
        }
        return current;
    }

    private static bool EqualsOperand(JsonElement field, Condition cond)
        => ScalarEquals(field, cond.StringValue);

    private static bool ScalarEquals(JsonElement field, string operand)
    {
        var trimmed = operand.Trim('\'', '"');
        switch (field.ValueKind)
        {
            case JsonValueKind.Number:
                return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                    && Math.Abs(field.GetDouble() - num) < 1e-9;
            case JsonValueKind.True:
            case JsonValueKind.False:
                return bool.TryParse(trimmed, out var b) && field.GetBoolean() == b;
            case JsonValueKind.String:
                var str = field.GetString() ?? string.Empty;
                if (string.Equals(str, trimmed, StringComparison.Ordinal))
                    return true;
                // ISO timestamps compare by instant, not by formatting (e.g. trailing Z vs +00:00)
                return TryParseDate(str, out var fieldDate)
                    && TryParseDate(trimmed, out var operandDate)
                    && fieldDate == operandDate;
            default:
                return false;
        }
    }

    private static bool InOperand(JsonElement field, Condition cond)
    {
        // JSON form carries a typed array; querystring form is pipe-separated
        if (cond.JsonValue is { ValueKind: JsonValueKind.Array } array)
            return array.EnumerateArray().Any(item => ScalarEquals(field, JsonScalarToString(item)));

        return cond.StringValue
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Any(value => ScalarEquals(field, value));
    }

    private static int? Compare(JsonElement field, Condition cond)
    {
        var operand = cond.StringValue.Trim('\'', '"');

        if (field.ValueKind == JsonValueKind.Number)
        {
            return double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                ? field.GetDouble().CompareTo(num)
                : null;
        }

        if (field.ValueKind == JsonValueKind.String)
        {
            var str = field.GetString() ?? string.Empty;

            // Date-like strings compare by instant so mixed offsets/formats order correctly
            if (TryParseDate(str, out var fieldDate) && TryParseDate(operand, out var operandDate))
                return fieldDate.CompareTo(operandDate);

            // A numeric operand against a numeric string field compares numerically
            if (double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out var opNum)
                && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var fieldNum))
                return fieldNum.CompareTo(opNum);

            return string.CompareOrdinal(str, operand);
        }

        return null;
    }

    private static bool TryParseDate(string value, out DateTimeOffset result)
    {
        // Require a date-like shape to avoid parsing plain numbers or words as dates
        if (value.Length < 8 || !value.Contains('-'))
        {
            result = default;
            return false;
        }
        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result);
    }

    private static bool RegexMatches(JsonElement field, Condition cond)
    {
        var input = field.ValueKind switch
        {
            JsonValueKind.String => field.GetString() ?? string.Empty,
            JsonValueKind.Number => field.GetRawText(),
            _ => null,
        };
        if (input is null)
            return false;

        var pattern = cond.StringValue;
        var flags = cond.RegexOptions ?? string.Empty;

        // Accept the /pattern/flags literal form
        var literal = Regex.Match(pattern, "^/(.*)/([a-z]*)$");
        if (literal.Success)
        {
            pattern = literal.Groups[1].Value;
            flags = literal.Groups[2].Value;
        }

        var options = RegexOptions.None;
        if (flags.Contains('i'))
            options |= RegexOptions.IgnoreCase;
        if (flags.Contains('m'))
            options |= RegexOptions.Multiline;

        try
        {
            return Regex.IsMatch(input, pattern, options, RegexTimeout);
        }
        catch (Exception)
        {
            // Invalid pattern or timeout: no match, never an error (legacy behavior)
            return false;
        }
    }

    #endregion
}
