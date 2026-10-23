using System.Collections.Frozen;
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

    private static readonly HashSet<string> KnownOps =
    [
        "$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$in", "$nin", "$regex", "$exists",
    ];

    /// <summary>
    /// Wire-shape serializer options: camelCase like ASP.NET's defaults, with the models'
    /// <c>[JsonPropertyName]</c> attributes taking precedence.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex RegexLiteral = new("^/(.*)/([a-z]*)$", RegexOptions.NonBacktracking);

    /// <summary>
    /// Treatment fields on which legacy Nightscout reads a bare <c>/pattern/flags</c> value as a
    /// regular expression. Pass to <see cref="Parse(string?, IReadOnlySet{string}?)"/> from
    /// treatment queries only: other collections compare such a value literally.
    /// </summary>
    public static readonly IReadOnlySet<string> TreatmentBareRegexFields =
        new[] { "eventType", "notes", "enteredBy" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Synthetic operator for a find[...] key, operator, empty operand object or <c>$and</c>/<c>$or</c>
    /// value the parser couldn't interpret. It never matches, so an unsupported filter narrows results
    /// to nothing (fail closed) instead of being dropped. MongoDB matches nothing for an empty operand
    /// object and refuses the rest.
    /// </summary>
    private const string UnsupportedOp = "$unsupported";

    private static readonly Condition Unsupported = new("", UnsupportedOp, "", JsonValue: null, RegexOptions: null);

    /// <summary>An empty query: no time bounds, no field filters, matches everything.</summary>
    public static readonly FindQuery Empty = new(new Group(IsAnd: true, []), TimeFields, null, null);

    private readonly Group _root;
    private readonly IReadOnlySet<string> _boundFields;

    private FindQuery(Group root, IReadOnlySet<string> boundFields, long? fromMills, long? toMills)
    {
        _root = root;
        _boundFields = boundFields;
        FromMills = fromMills;
        ToMills = toMills;
    }

    /// <summary>Inclusive lower time bound in Unix milliseconds, if the query constrains one.</summary>
    public long? FromMills { get; }

    /// <summary>Inclusive upper time bound in Unix milliseconds, if the query constrains one.</summary>
    public long? ToMills { get; }

    /// <summary>
    /// The modification stamp, in Unix milliseconds, a modified-since read resumes after, when the
    /// query is nothing but lower bounds on <c>srvModified</c>: <c>$gt</c> v is after v, and
    /// <c>$gte</c> v is after v - 1. Any other condition leaves it null, since the read pages by stamp
    /// and cannot apply one.
    /// </summary>
    public long? ModifiedAfterMills
    {
        get
        {
            long? after = null;
            var bounds = 0;
            foreach (var cond in EnumerateAndReachable(_root).Where(c => c.Path == "srvModified"))
            {
                if (cond.Op is not ("$gt" or "$gte")
                    || !long.TryParse(cond.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamp))
                    return null;

                bounds++;
                after = Max(after, cond.Op == "$gt" ? stamp : stamp - 1);
            }

            return bounds > 0 && bounds == CountConditions(_root) ? after : null;
        }
    }

    private static int CountConditions(Group group) =>
        group.Children.Sum(child => child is Condition ? 1 : child is Group nested ? CountConditions(nested) : 0);

    /// <summary>
    /// <c>true</c> when the query carries no condition a caller could honour: nothing at all, only
    /// empty <c>$and</c>/<c>$or</c> groups or operand objects, or only <c>find[…]</c> keys the parser
    /// could not interpret.
    /// </summary>
    public bool IsEmpty => !HasLeafCondition(_root);

    private static bool HasLeafCondition(Group group) =>
        group.Children.Any(child => child is Condition cond
            ? cond.Op != UnsupportedOp
            : child is Group nested && HasLeafCondition(nested));

    /// <summary>
    /// <c>true</c> when a condition cannot be evaluated as legacy Nightscout evaluates it: a find key,
    /// operator, empty operand object, <c>$options</c> without a <c>$regex</c> or <c>$and</c>/<c>$or</c>
    /// value the parser could not read, a <c>$regex</c> that does not compile (see
    /// <see cref="BoundedRegex"/>), an <c>$exists</c> whose operand is not a boolean, or a
    /// condition on a time field whose operand is not an instant, which Nightscout refuses. A read may
    /// serve such a query; a delete must refuse it.
    /// </summary>
    public bool HasInexactCondition => EnumerateAll(_root).Any(cond =>
        !KnownOps.Contains(cond.Op)
        || (cond.Op == "$regex" && !cond.Pattern.IsCompiled)
        || (cond.Op == "$exists" && TryReadExists(cond) is null)
        || (cond.Op != "$exists" && TimeFields.Contains(cond.Path) && !TryConvertToInstant(cond, out _, out _)));

    /// <summary>
    /// <c>true</c> when a <c>$regex</c> was abandoned mid-scan (see <see cref="BoundedRegex.Abandoned"/>),
    /// so the rows matched so far may be fewer than the query asks for.
    /// </summary>
    public bool HasAbandonedRegex => EnumerateAll(_root).Any(cond => cond.Op == "$regex" && cond.Pattern.Abandoned);

    /// <summary>
    /// The presence an <c>$exists</c> asks for, or <c>null</c> for an operand that is not a boolean.
    /// MongoDB reads such an operand by its truthiness and Nightscout passes a query-string one on
    /// as a string, so no reading of it here is safe to act on: it matches nothing.
    /// </summary>
    private static bool? TryReadExists(Condition cond) => cond.StringValue.ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    /// <summary>
    /// The field paths the query's conditions name, so a caller that can filter only some fields can
    /// refuse the rest instead of dropping them.
    /// </summary>
    public IReadOnlySet<string> Fields =>
        EnumerateAll(_root).Where(cond => cond.Op != UnsupportedOp).Select(cond => cond.Path).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<Condition> EnumerateAll(Group group) =>
        group.Children.SelectMany(child => child switch
        {
            Condition cond => [cond],
            Group nested => EnumerateAll(nested),
            _ => [],
        });

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
    /// <param name="exceptField">The field whose equality the caller applies itself.</param>
    /// <param name="comparison">
    /// How a condition's path is matched to <paramref name="exceptField"/>. Ordinal for a caller
    /// that applies the equality to a store, since MongoDB field names are case-sensitive.
    /// </param>
    public bool HasFieldFiltersExcept(
        string? exceptField, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        // Only a single consistent equality can be excepted: contradictory equalities on the
        // field (a match-nothing query) must stay residual, or excepting them would silently
        // widen the query — and, on the delete path, widen it into a whole-window sweep.
        if (exceptField != null && GetEqualityValue(exceptField, comparison) is null)
            exceptField = null;

        return CountResidual(_root, andReachable: true, exceptField, comparison, _boundFields) > 0;
    }

    /// <summary>
    /// Returns the value of a top-level (AND-reachable) <c>$eq</c> condition on
    /// <paramref name="field"/>, or <c>null</c> when the query doesn't constrain the field to a
    /// single equality.
    /// </summary>
    public string? GetEqualityValue(string field, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        string? found = null;
        foreach (var cond in EnumerateAndReachable(_root).Where(c => c.Path.Equals(field, comparison)))
        {
            if (cond.Op != "$eq")
                return null;
            if (found != null && found != cond.StringValue)
                return null;
            found = cond.StringValue;
        }
        return found;
    }

    /// <summary>
    /// The values <paramref name="field"/> may take when every AND-reachable condition on it is an
    /// <c>$eq</c> or <c>$in</c>, ordinally named: their intersection, possibly empty. <c>null</c>
    /// when the field is unconstrained or constrained any other way.
    /// </summary>
    public IReadOnlyList<string>? GetEqualityValues(string field)
    {
        List<string>? allowed = null;
        foreach (var cond in EnumerateAndReachable(_root).Where(c => c.Path.Equals(field, StringComparison.Ordinal)))
        {
            IEnumerable<string>? values = cond.Op switch
            {
                "$eq" => new[] { cond.StringValue },
                "$in" when cond.JsonValue is { ValueKind: JsonValueKind.Array } array =>
                    array.EnumerateArray().Select(JsonScalarToString),
                "$in" => cond.StringValue.Split('|', StringSplitOptions.RemoveEmptyEntries),
                _ => null,
            };
            if (values is null)
                return null;

            allowed = allowed is null ? values.Distinct().ToList() : allowed.Intersect(values).ToList();
        }
        return allowed;
    }

    /// <summary>
    /// <c>true</c> when the query's top level (outside any <c>$and</c>/<c>$or</c> group) carries a
    /// condition on <paramref name="field"/> or one of its sub-paths — the same test as legacy
    /// Nightscout's truthiness check on <c>query[field]</c>. Field names are case-sensitive, as in
    /// MongoDB.
    /// </summary>
    public bool HasTopLevelCondition(string field)
    {
        return EnumerateTopLevel(_root).Any(c =>
            c.Path.Equals(field, StringComparison.Ordinal)
            || c.Path.StartsWith(field + ".", StringComparison.Ordinal));
    }

    /// <summary>
    /// Conditions outside every logical group. A JSON field operand object
    /// (<c>{"created_at":{"$gte":…}}</c>) parses to a non-logical group and is descended into.
    /// </summary>
    private static IEnumerable<Condition> EnumerateTopLevel(Group group)
    {
        foreach (var child in group.Children)
        {
            if (child is Condition cond)
                yield return cond;
            else if (child is Group { IsLogical: false } nested)
                foreach (var inner in EnumerateTopLevel(nested))
                    yield return inner;
        }
    }

    /// <summary>
    /// Parses a find query from either wire form. A null/empty/unparseable input yields
    /// <see cref="Empty"/> (no filtering), matching the legacy server's lenient behavior.
    /// </summary>
    public static FindQuery Parse(string? find) => Parse(find, bareRegexFields: null);

    /// <summary>
    /// Parses a find query, additionally reading a bare querystring operand of the form
    /// <c>/pattern/flags</c> on one of <paramref name="bareRegexFields"/> as a <c>$regex</c>.
    /// Only top-level fields are read this way, not those inside <c>$and</c>/<c>$or</c> clauses. An explicit operator is never rewritten.
    /// </summary>
    /// <param name="find">The find in either wire form.</param>
    /// <param name="bareRegexFields">Fields whose bare <c>/pattern/flags</c> operand is a regex.</param>
    /// <param name="boundFields">
    /// The time fields whose bounds become <see cref="FromMills"/>/<see cref="ToMills"/>, when the
    /// caller's source filters on a time only some of them carry. A bound on any other time field
    /// stays residual. Defaults to every recognised time field.
    /// </param>
    public static FindQuery Parse(
        string? find, IReadOnlySet<string>? bareRegexFields, IReadOnlySet<string>? boundFields = null)
    {
        boundFields ??= TimeFields;

        if (string.IsNullOrWhiteSpace(find) || find.Trim() == "{}")
            return Empty;

        try
        {
            // JSON form is checked first: a JSON find could contain the literal "find[" inside
            // an operand (e.g. a $regex pattern) and must not be misrouted to the querystring parser.
            Group root;
            if (find.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(find);
                root = ParseJsonObject(doc.RootElement, path: null, depth: 0);
            }
            else if (find.Contains("find[", StringComparison.OrdinalIgnoreCase)
                || find.Contains("find%5B", StringComparison.OrdinalIgnoreCase))
            {
                root = ParseQueryString(find, bareRegexFields);
            }
            else
            {
                return Empty;
            }

            var (from, to) = ExtractTimeBounds(root, boundFields);
            return new FindQuery(root, boundFields, from, to);
        }
        catch (Exception)
        {
            // Malformed queries never break a read; legacy Nightscout silently ignores them.
            return Empty;
        }
    }

    /// <summary>
    /// This method gives a query object with the search conditions.
    /// The query object keeps the AND and OR groups and the query values.
    /// The regex text does not change before the search.
    /// </summary>
    public Dictionary<string, object> ToQueryObject() {
        return ToQueryObject(_root);
    }

    private static Dictionary<string, object> ToQueryObject(Group group) {
        if (group.Children.Count == 0) {
            return new Dictionary<string, object>();
        }

        var childQueries = new Dictionary<string, object>[group.Children.Count];

        for (int i = 0; i < group.Children.Count; i++) {
            object child = group.Children[i];

            if (child is Group) {
                childQueries[i] = ToQueryObject((Group)child);
            } else {
                childQueries[i] = ToQueryObject((Condition)child);
            }
        }

        string groupOperator = group.IsAnd ? "$and" : "$or";
        return new Dictionary<string, object> { [groupOperator] = childQueries };
    }

    private static Dictionary<string, object> ToQueryObject(Condition condition) {
        string operatorName = condition.Op;

        bool isSupportedOperator =
            operatorName == "$eq" || operatorName == "$ne" ||
            operatorName == "$gt" || operatorName == "$gte" ||
            operatorName == "$lt" || operatorName == "$lte" ||
            operatorName == "$in" || operatorName == "$nin" ||
            operatorName == "$exists" || operatorName == "$regex";

        if (!isSupportedOperator) {
            // These filters must give no matches, also for documents without an _id.
            return new Dictionary<string, object> {
                ["_id"] = new Dictionary<string, object> { ["$in"] = Array.Empty<string>() }
            };
        }

        var operators = new Dictionary<string, object> { [operatorName] = ToQueryOperand(condition) };

        if (operatorName == "$regex" && condition.RegexOptions != null) {
            operators["$options"] = condition.RegexOptions;
        }

        return new Dictionary<string, object> { [condition.Path] = operators };
    }

    private static object ToQueryOperand(Condition condition) {
        object operand = condition.StringValue;

        if (condition.JsonValue.HasValue) {
            operand = condition.JsonValue.Value;
        }

        bool isListOperator = condition.Op == "$in" || condition.Op == "$nin";

        if (isListOperator) {
            bool isJsonArray = condition.JsonValue.HasValue &&
                condition.JsonValue.Value.ValueKind == JsonValueKind.Array;

            if (!isJsonArray) {
                operand = condition.StringValue.Split('|', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        return operand;
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
        return EvaluateGroup(_root, document, _boundFields);
    }

    /// <summary>
    /// Like <see cref="Matches{T}(T)"/>, with top-level <paramref name="field"/> read as
    /// <paramref name="value"/>: for a document known by a value its wire shape rewrites, such as a
    /// stored id the wire form coerces to an ObjectId.
    /// </summary>
    public bool MatchesAs<T>(T document, string field, string value)
    {
        var node = JsonSerializer.SerializeToNode(document, WireOptions)!.AsObject();
        node[field] = value;
        return Matches(JsonSerializer.SerializeToElement(node, WireOptions));
    }

    /// <summary>
    /// Like <see cref="Matches{T}(T)"/> but without the conditions already captured as
    /// <see cref="FromMills"/>/<see cref="ToMills"/>, for callers whose source applied those bounds
    /// to a time the document's own timestamp fields do not carry.
    /// </summary>
    public bool MatchesResidual<T>(T document)
    {
        return EvaluateGroup(
            _root, JsonSerializer.SerializeToElement(document, WireOptions), _boundFields,
            skipCapturedBounds: true, andReachable: true);
    }

    private sealed record Group(bool IsAnd, List<object> Children, bool IsLogical = false);

    /// <summary>
    /// A single field condition. <see cref="StringValue"/> carries the querystring-form operand;
    /// <see cref="JsonValue"/> the JSON-form operand (needed for typed arrays under $in/$nin).
    /// </summary>
    private sealed record Condition(string Path, string Op, string StringValue, JsonElement? JsonValue, string? RegexOptions)
    {
        private BoundedRegex? _pattern;

        /// <summary>
        /// The <c>$regex</c> operand, compiled on first use. A <c>with</c> copies it, so a condition is
        /// only rewritten while parsing, before anything reads it.
        /// </summary>
        public BoundedRegex Pattern => _pattern ??= CompileRegex(StringValue, RegexOptions);
    }

    private static BoundedRegex CompileRegex(string pattern, string? flags)
    {
        var literal = RegexLiteral.Match(pattern);
        if (literal.Success)
        {
            pattern = literal.Groups[1].Value;
            flags = literal.Groups[2].Value;
        }

        var options = RegexOptions.None;
        if (flags?.Contains('i') == true)
            options |= RegexOptions.IgnoreCase;
        if (flags?.Contains('m') == true)
            options |= RegexOptions.Multiline;

        return BoundedRegex.Create(pattern, options);
    }

    private static Group ParseQueryString(string queryString, IReadOnlySet<string>? bareRegexFields)
    {
        var parsed = HttpUtility.ParseQueryString(queryString);

        // First pass: flatten find[...] keys into (groupKey, field, op, value) entries.
        // groupKey is "" for top-level conditions, "and:0"/"or:1" for logical group clauses.
        // A find[...] key that fits no recognised shape becomes a match-nothing condition:
        // silently dropping it would widen the query — and, on the delete path, widen a
        // field-filtered delete into a whole-window sweep.
        var entries = new List<(string GroupKey, string Field, string Op, string Value)>();
        var findKeys = parsed.AllKeys
            .OfType<string>()
            .Where(k => k.StartsWith("find[", StringComparison.OrdinalIgnoreCase));
        foreach (var key in findKeys)
        {
            var values = parsed.GetValues(key);
            if (values is null)
                continue;

            if (!TryParseFindKey(key, out var groupKey, out var field, out var op, out var opIsImplicit))
            {
                entries.Add(("", "", UnsupportedOp, ""));
                continue;
            }

            var readsBareRegex = groupKey.Length == 0 && opIsImplicit && bareRegexFields?.Contains(field) == true;
            foreach (var value in values)
                entries.Add(readsBareRegex && RegexLiteral.IsMatch(value)
                    ? (groupKey, field, "$regex", value)
                    : (groupKey, field, op, value));
        }

        // Repeated $in/$nin params for one field form a single value set, not AND'ed conditions
        entries = entries
            .GroupBy(e => (e.GroupKey, e.Field, e.Op))
            .SelectMany(g => g.Key.Op is "$in" or "$nin"
                ? new[] { (g.Key.GroupKey, g.Key.Field, g.Key.Op, string.Join("|", g.Select(e => e.Value))) }
                : g.AsEnumerable())
            .ToList();

        // $options entries modify the sibling $regex on the same field rather than standing alone
        var regexOptions = new Dictionary<(string GroupKey, string Field), string>();
        foreach (var entry in entries.Where(e => e.Op == "$options"))
            regexOptions[(entry.GroupKey, entry.Field.ToLowerInvariant())] = entry.Value;

        // Second pass: build the tree. Clauses sharing a group index are AND'd internally;
        // "or:*" clauses combine under a single OR group, "and:*" clauses join the root AND.
        var root = new Group(IsAnd: true, []);
        var clauses = new Dictionary<string, Group>();
        Group? orGroup = null;

        foreach (var (groupKey, field, op, value) in entries.Where(e => e.Op != "$options"))
        {
            var owner = root;
            if (groupKey.Length > 0)
            {
                if (!clauses.TryGetValue(groupKey, out var clause))
                {
                    clause = new Group(IsAnd: true, [], IsLogical: true);
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

        // MongoDB refuses an $options with no $regex beside it
        var regexKeys = entries.Where(e => e.Op == "$regex").Select(e => (e.GroupKey, e.Field.ToLowerInvariant())).ToHashSet();
        if (regexOptions.Keys.Any(key => !regexKeys.Contains(key)))
            root.Children.Add(Unsupported);

        return root;
    }

    private static Group AddChildGroup(Group parent, bool isAnd)
    {
        var child = new Group(isAnd, [], IsLogical: true);
        parent.Children.Add(child);
        return child;
    }

    /// <summary>
    /// The bracketed segments of a <c>find[a][b]</c> querystring key, a trailing <c>[]</c> array
    /// marker included as an empty segment, or null when the key is not a find key.
    /// </summary>
    public static List<string>? SplitFindKey(string key) =>
        Regex.IsMatch(key, @"^find(\[[^\]]*\])+$")
            ? Regex.Matches(key, @"\[([^\]]*)\]").Select(m => m.Groups[1].Value).ToList()
            : null;

    /// <summary>
    /// Parses a single querystring key into its group ("", "and:N", "or:N"), dotted field path,
    /// and operator. Handles <c>find[field]</c>, <c>find[field][$op]</c>, nested paths
    /// (<c>find[a][b]</c> → <c>a.b</c>), the <c>find[field][$in][]</c> array marker, and
    /// <c>find[$and|$or][N][field…][$op]</c> group clauses.
    /// </summary>
    private static bool TryParseFindKey(
        string key, out string groupKey, out string field, out string op, out bool opIsImplicit)
    {
        groupKey = "";
        field = "";
        op = "$eq";
        opIsImplicit = true;

        if (SplitFindKey(key) is not { } segments)
            return false;

        // Trailing [] is an array marker (find[f][$in][])
        if (segments.Count > 1 && segments[^1].Length == 0)
            segments.RemoveAt(segments.Count - 1);

        // Logical group clauses: find[$and][N][field…][$op]
        if (segments[0] is "$and" or "$or")
        {
            if (segments.Count < 3 || segments[1].Length == 0 || !segments[1].All(char.IsAsciiDigit))
                return false;
            groupKey = $"{segments[0][1..]}:{segments[1]}";
            segments = segments.GetRange(2, segments.Count - 2);
        }

        // A trailing $-segment is the operator; everything before it is the (dotted) field path
        if (segments[^1].StartsWith('$'))
        {
            op = segments[^1];
            opIsImplicit = false;
            segments.RemoveAt(segments.Count - 1);
        }

        if (segments.Count == 0 || segments.Any(s => s.Length == 0 || s.StartsWith('$')))
            return false;

        field = string.Join(".", segments);
        return true;
    }

    private const int MaxDepth = 10;

    private static Group ParseJsonObject(JsonElement element, string? path, int depth)
    {
        if (depth > MaxDepth)
            return new Group(IsAnd: true, [Unsupported]);

        var group = new Group(IsAnd: true, []);
        string? pendingRegexOptions = null;
        var sawOptions = false;
        Condition? pendingRegex = null;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "$and":
                case "$or":
                    if (property.Value.ValueKind != JsonValueKind.Array
                        || property.Value.GetArrayLength() == 0
                        || property.Value.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.Object))
                    {
                        group.Children.Add(Unsupported);
                        break;
                    }

                    var logical = new Group(IsAnd: property.Name == "$and", [], IsLogical: true);
                    foreach (var item in property.Value.EnumerateArray())
                        logical.Children.Add(ParseJsonObject(item, path: null, depth + 1));
                    group.Children.Add(logical);
                    break;

                case "$options" when path != null:
                    sawOptions = true;
                    pendingRegexOptions = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
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

                case var name when name.StartsWith('$'):
                    group.Children.Add(Unsupported);
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

        if ((path != null && group.Children.Count == 0) || (sawOptions && pendingRegex == null))
            group.Children.Add(Unsupported);

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

    /// <remarks>
    /// A document's time is its whole millisecond, so a strict or sub-millisecond operand rounds
    /// inward and the inclusive window stays exact: <c>$gte 0.5ms</c> starts at 1ms, <c>$lt 0.5ms</c>
    /// ends at 0ms, and <c>$eq 0.5ms</c> is empty.
    /// </remarks>
    private static (long? From, long? To) ExtractTimeBounds(Group root, IReadOnlySet<string> boundFields)
    {
        long? from = null;
        long? to = null;

        foreach (var cond in EnumerateAndReachable(root).Where(c => boundFields.Contains(c.Path)))
        {
            if (!TryConvertToInstant(cond, out var mills, out var wholeMillisecond))
                continue;

            var ceiling = wholeMillisecond ? mills : Next(mills);
            switch (cond.Op)
            {
                case "$gte":
                    from = Max(from, ceiling);
                    break;
                case "$gt":
                    from = Max(from, Next(mills));
                    break;
                case "$lte":
                    to = Min(to, mills);
                    break;
                case "$lt":
                    to = Min(to, Previous(ceiling));
                    break;
                case "$eq":
                    from = Max(from, ceiling);
                    to = Min(to, mills);
                    break;
            }
        }

        return (from, to);
    }

    private static long Next(long mills) => mills == long.MaxValue ? mills : mills + 1;

    private static long Previous(long mills) => mills == long.MinValue ? mills : mills - 1;

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
    private static int CountResidual(
        Group group, bool andReachable, string? exceptField, StringComparison comparison,
        IReadOnlySet<string> boundFields)
    {
        var count = 0;
        foreach (var child in group.Children)
        {
            switch (child)
            {
                case Condition cond:
                    var isCapturedTimeBound = IsCapturedTimeBound(cond, andReachable, boundFields);
                    var isExceptedEquality = exceptField != null
                        && cond.Op == "$eq"
                        && andReachable
                        && cond.Path.Equals(exceptField, comparison);
                    if (!isCapturedTimeBound && !isExceptedEquality)
                        count++;
                    break;
                case Group nested:
                    count += CountResidual(nested, andReachable && nested.IsAnd, exceptField, comparison, boundFields);
                    break;
            }
        }
        return count;
    }

    private static bool IsCapturedTimeBound(Condition cond, bool andReachable, IReadOnlySet<string> boundFields) =>
        andReachable
        && boundFields.Contains(cond.Path)
        && cond.Op is "$eq" or "$gt" or "$gte" or "$lt" or "$lte"
        && TryConvertToInstant(cond, out _, out _);

    /// <summary>
    /// Reads the operand as an instant: <paramref name="mills"/> is its millisecond, rounded down,
    /// and <paramref name="wholeMillisecond"/> says nothing was rounded away.
    /// </summary>
    private static bool TryConvertToInstant(Condition cond, out long mills, out bool wholeMillisecond)
    {
        mills = 0;
        wholeMillisecond = true;

        // Numeric operand: epoch milliseconds (the wire form for date/mills)
        if (long.TryParse(cond.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            mills = numeric;
            return true;
        }

        // ISO 8601 operand (created_at / dateString)
        if (!TryParseOperandDate(cond.StringValue.Trim('\'', '"'), out var parsed))
            return false;

        mills = parsed.ToUnixTimeMilliseconds();
        wholeMillisecond = parsed.UtcTicks % TimeSpan.TicksPerMillisecond == 0;
        return true;
    }

    /// <summary>
    /// An unencoded <c>+hh:mm</c> offset arrives from the query string as a space, which Nightscout
    /// turns back into the <c>+</c> before it parses the date.
    /// </summary>
    private static bool TryParseOperandDate(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed)
        || (value.LastIndexOf(' ') is var space and > 0
            && DateTimeOffset.TryParse(
                string.Concat(value.AsSpan(0, space), "+", value.AsSpan(space + 1)),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed));

    private static bool EvaluateGroup(
        Group group, JsonElement document, IReadOnlySet<string> boundFields,
        bool skipCapturedBounds = false, bool andReachable = true)
    {
        if (group.Children.Count == 0)
            return true;

        return group.IsAnd
            ? group.Children.All(child => EvaluateChild(child, document, boundFields, skipCapturedBounds, andReachable))
            : group.Children.Any(child => EvaluateChild(child, document, boundFields, skipCapturedBounds, andReachable));
    }

    private static bool EvaluateChild(
        object child, JsonElement document, IReadOnlySet<string> boundFields,
        bool skipCapturedBounds, bool andReachable) => child switch
    {
        Condition cond => (skipCapturedBounds && IsCapturedTimeBound(cond, andReachable, boundFields))
            || EvaluateCondition(cond, document),
        Group nested => EvaluateGroup(
            nested, document, boundFields, skipCapturedBounds, andReachable && nested.IsAnd),
        _ => true,
    };

    private static bool EvaluateCondition(Condition cond, JsonElement document)
    {
        var field = ResolveField(document, cond.Path);
        var missing = field is null || field.Value.ValueKind == JsonValueKind.Null;
        var operandIsNull = cond.JsonValue is { ValueKind: JsonValueKind.Null };

        switch (cond.Op)
        {
            case "$exists":
                return TryReadExists(cond) is { } wantExists && wantExists != missing;

            // Mongo semantics: negative operators match documents missing the field, and a null
            // operand means "absent or null" for $eq / "present and non-null" for $ne
            case "$ne":
                return operandIsNull ? !missing : missing || !EqualsOperand(field!.Value, cond);
            case "$nin":
                return missing || !InOperand(field!.Value, cond);
            case "$eq" when operandIsNull:
                return missing;
        }

        if (missing)
            return false;

        return cond.Op switch
        {
            "$eq" => EqualsOperand(field!.Value, cond),
            "$in" => InOperand(field!.Value, cond),
            "$gt" => CompareOperand(field!.Value, cond) is > 0,
            "$gte" => CompareOperand(field!.Value, cond) is >= 0,
            "$lt" => CompareOperand(field!.Value, cond) is < 0,
            "$lte" => CompareOperand(field!.Value, cond) is <= 0,
            "$regex" => RegexMatches(field!.Value, cond),
            _ => false,
        };
    }

    /// <summary>
    /// Compares a recognised time field against the operand on the epoch-millisecond axis, so a
    /// numeric field matches an ISO operand and vice versa. The pushdown already treats the
    /// representations interchangeably; in-memory evaluation must agree or a captured time bound
    /// re-checked inside a filtered page would reject every row.
    /// </summary>
    private static bool TryTimeCompare(JsonElement field, Condition cond, out int comparison)
    {
        comparison = 0;
        if (!TimeFields.Contains(cond.Path))
            return false;
        if (!TryConvertToInstant(cond, out var operandMills, out var wholeMillisecond))
            return false;

        long fieldMills;
        if (field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var numeric))
            fieldMills = numeric;
        else if (field.ValueKind == JsonValueKind.String
                 && TryParseDate(field.GetString() ?? string.Empty, out var date))
            fieldMills = date.ToUnixTimeMilliseconds();
        else
            return false;

        // A field is its whole millisecond, so it lies before an operand inside that millisecond.
        comparison = fieldMills.CompareTo(operandMills);
        if (comparison == 0 && !wholeMillisecond)
            comparison = -1;
        return true;
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
    {
        if (TryTimeCompare(field, cond, out var comparison))
            return comparison == 0;
        return ScalarEquals(field, cond.StringValue);
    }

    private static int? CompareOperand(JsonElement field, Condition cond)
    {
        if (TryTimeCompare(field, cond, out var comparison))
            return comparison;
        return Compare(field, cond);
    }

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
        return input is not null && cond.Pattern.IsMatch(input);
    }
}
