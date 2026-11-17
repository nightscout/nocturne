using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Compares HTTP responses for API parity testing.
/// Key behavior:
/// - Field order does NOT matter (JSON object comparison is unordered)
/// - Null vs missing IS strict ({"a": null} != {})
/// - Dynamic fields (IDs, timestamps) can be configured to skip value comparison
/// </summary>
public class ResponseComparer
{
    private readonly ComparisonOptions _options;

    public ResponseComparer(ComparisonOptions? options = null)
    {
        _options = options ?? ComparisonOptions.Default;
    }

    /// <summary>
    /// Compares two HTTP responses for parity
    /// </summary>
    public async Task<ComparisonResult> CompareAsync(
        HttpResponseMessage expected,
        HttpResponseMessage actual,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ComparisonResult { Context = context ?? "Response" };

        // Compare status codes
        if (expected.StatusCode != actual.StatusCode)
        {
            result.AddDifference(
                "StatusCode",
                ((int)expected.StatusCode).ToString(),
                ((int)actual.StatusCode).ToString());
        }

        // For error responses (4xx/5xx), if StatusCodeOnlyForErrors is enabled,
        // we only care that both returned the same error status code.
        // The body format differs between Nightscout (HTML) and ASP.NET (JSON ProblemDetails).
        var isErrorResponse = (int)expected.StatusCode >= 400;
        if (_options.StatusCodeOnlyForErrors && isErrorResponse && expected.StatusCode == actual.StatusCode)
        {
            return result; // Status codes match, skip body/header comparison for errors
        }

        // Compare configured headers
        foreach (var header in _options.HeadersToCompare)
        {
            var expectedValue = GetHeaderValue(expected, header);
            var actualValue = GetHeaderValue(actual, header);

            // For Content-Type, compare media type only (ignore charset and other parameters)
            if (header.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                var expectedMediaType = expectedValue?.Split(';')[0].Trim();
                var actualMediaType = actualValue?.Split(';')[0].Trim();

                if (!string.Equals(expectedMediaType, actualMediaType, StringComparison.OrdinalIgnoreCase))
                {
                    result.AddDifference($"Header[{header}]", expectedMediaType, actualMediaType);
                }
            }
            else if (!string.Equals(expectedValue, actualValue, StringComparison.OrdinalIgnoreCase))
            {
                result.AddDifference($"Header[{header}]", expectedValue, actualValue);
            }
        }

        // Compare response bodies
        var expectedBody = await expected.Content.ReadAsStringAsync(cancellationToken);
        var actualBody = await actual.Content.ReadAsStringAsync(cancellationToken);

        CompareJsonBodies(expectedBody, actualBody, "Body", result);

        return result;
    }

    private static string? GetHeaderValue(HttpResponseMessage response, string header)
    {
        if (response.Headers.TryGetValues(header, out var values))
            return string.Join(",", values);
        if (response.Content.Headers.TryGetValues(header, out var contentValues))
            return string.Join(",", contentValues);
        return null;
    }

    private void CompareJsonBodies(string? expected, string? actual, string path, ComparisonResult result)
    {
        var expectedEmpty = string.IsNullOrWhiteSpace(expected);
        var actualEmpty = string.IsNullOrWhiteSpace(actual);

        if (expectedEmpty && actualEmpty)
            return;

        if (expectedEmpty != actualEmpty)
        {
            result.AddDifference(path, expected ?? "(empty)", actual ?? "(empty)");
            return;
        }

        JsonNode? expectedNode, actualNode;

        // Try to parse as JSON; if both fail, compare as raw strings
        var expectedIsJson = TryParseJson(expected!, out expectedNode);
        var actualIsJson = TryParseJson(actual!, out actualNode);

        if (!expectedIsJson && !actualIsJson)
        {
            // Both are non-JSON (e.g., HTML), compare as strings
            if (!string.Equals(expected, actual))
            {
                result.AddDifference(path, expected, actual);
            }
            return;
        }

        if (!expectedIsJson)
        {
            result.AddDifference(path, $"Non-JSON: {Truncate(expected!, 100)}", $"JSON: {Truncate(actual!, 100)}");
            return;
        }

        if (!actualIsJson)
        {
            result.AddDifference(path, $"JSON: {Truncate(expected!, 100)}", $"Non-JSON: {Truncate(actual!, 100)}");
            return;
        }

        CompareNodes(expectedNode, actualNode, path, result);
    }

    private static bool TryParseJson(string value, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private void CompareNodes(JsonNode? expected, JsonNode? actual, string path, ComparisonResult result)
    {
        // Both null (JSON null, not missing)
        if (expected is null && actual is null)
            return;

        // One is null, other is not - this is a difference
        if (expected is null || actual is null)
        {
            result.AddDifference(path,
                expected?.ToJsonString() ?? "null",
                actual?.ToJsonString() ?? "null");
            return;
        }

        // Type mismatch
        if (expected.GetType() != actual.GetType())
        {
            result.AddDifference(path,
                $"({expected.GetType().Name}) {expected.ToJsonString()}",
                $"({actual.GetType().Name}) {actual.ToJsonString()}");
            return;
        }

        switch (expected)
        {
            case JsonObject expectedObj:
                CompareObjects(expectedObj, (JsonObject)actual, path, result);
                break;

            case JsonArray expectedArr:
                CompareArrays(expectedArr, (JsonArray)actual, path, result);
                break;

            case JsonValue expectedVal:
                CompareValues(expectedVal, (JsonValue)actual, path, result);
                break;
        }
    }

    private void CompareObjects(JsonObject expected, JsonObject actual, string path, ComparisonResult result)
    {
        // Get all unique keys from both objects
        var allKeys = expected.Select(p => p.Key)
            .Union(actual.Select(p => p.Key))
            .Distinct()
            .ToList();

        foreach (var key in allKeys)
        {
            var childPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";

            // Check if key should be skipped entirely (dynamic field)
            if (_options.DynamicFields.Contains(key))
                continue;

            var expectedHasKey = expected.ContainsKey(key);
            var actualHasKey = actual.ContainsKey(key);

            // Handle missing/extra fields
            if (expectedHasKey != actualHasKey)
            {
                // If AllowExtraActualFields is set and actual has a field that expected doesn't,
                // that's acceptable (Nocturne returning more data than Nightscout)
                if (_options.AllowExtraActualFields && !expectedHasKey && actualHasKey)
                    continue;

                // STRICT NULL VS MISSING: This is the critical check
                // A field being present with null value is different from field being absent
                var expectedDisplay = expectedHasKey
                    ? (expected[key]?.ToJsonString() ?? "null")
                    : "(missing)";
                var actualDisplay = actualHasKey
                    ? (actual[key]?.ToJsonString() ?? "null")
                    : "(missing)";

                result.AddDifference(childPath, expectedDisplay, actualDisplay);
                continue;
            }

            // Both have the key, compare values
            if (expectedHasKey)
            {
                CompareNodes(expected[key], actual[key], childPath, result);
            }
        }
    }

    private void CompareArrays(JsonArray expected, JsonArray actual, string path, ComparisonResult result)
    {
        if (expected.Count != actual.Count)
        {
            result.AddDifference($"{path}.length",
                expected.Count.ToString(),
                actual.Count.ToString());
        }

        var minLength = Math.Min(expected.Count, actual.Count);
        for (var i = 0; i < minLength; i++)
        {
            CompareNodes(expected[i], actual[i], $"{path}[{i}]", result);
        }
    }

    private void CompareValues(JsonValue expected, JsonValue actual, string path, ComparisonResult result)
    {
        var fieldName = path.Split('.').Last().TrimEnd(']').Split('[').First();

        // Check if this is a timestamp field - verify format only, not exact value
        if (_options.TimestampFields.Contains(fieldName))
        {
            var expectedStr = expected.ToJsonString();
            var actualStr = actual.ToJsonString();

            if (IsValidTimestamp(expectedStr) && IsValidTimestamp(actualStr))
                return; // Both valid timestamps, consider equal
        }

        // Check if this is an ID field that should only verify presence
        if (_options.IdFields.Contains(fieldName))
        {
            // Just verify both have non-empty values
            var expectedStr = expected.ToJsonString().Trim('"');
            var actualStr = actual.ToJsonString().Trim('"');

            if (!string.IsNullOrEmpty(expectedStr) && !string.IsNullOrEmpty(actualStr))
                return;
        }

        if (expected.TryGetValue<string>(out var expectedText) && actual.TryGetValue<string>(out var actualText))
        {
            foreach (var normaliser in _options.ValueNormalisers.Where(n => n.Fields.Contains(fieldName)))
            {
                expected = JsonValue.Create(normaliser.Pattern.Replace(expectedText, normaliser.Replacement));
                actual = JsonValue.Create(normaliser.Pattern.Replace(actualText, normaliser.Replacement));
                expectedText = expected.GetValue<string>();
                actualText = actual.GetValue<string>();
            }
        }

        // Standard value comparison
        var expectedJson = expected.ToJsonString();
        var actualJson = actual.ToJsonString();

        if (expectedJson != actualJson
            && !(ReloadLagToleranceFor(path) is { } tolerance && IsWithinTolerance(expectedJson, actualJson, tolerance)))
        {
            result.AddDifference(path, expectedJson, actualJson);
        }
    }

    private static readonly Regex NumberToken = new(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled);

    private decimal? ReloadLagToleranceFor(string path)
    {
        var segments = path.Split('.');
        foreach (var (pattern, tolerance) in _options.ReloadLagTolerances)
        {
            var patternSegments = pattern.Split('.');
            if (patternSegments.Length == segments.Length
                && patternSegments.Zip(segments).All(pair => pair.First == "*" || pair.First == pair.Second))
                return tolerance;
        }

        return null;
    }

    private static bool IsWithinTolerance(string expected, string actual, decimal tolerance)
    {
        var expectedNumbers = NumberToken.Matches(expected);
        var actualNumbers = NumberToken.Matches(actual);
        if (expectedNumbers.Count != 1 || actualNumbers.Count != 1)
            return false;

        if (NumberToken.Replace(expected, "#") != NumberToken.Replace(actual, "#"))
            return false;

        // A display string is formatted text, so "0" against "0.00" is a difference in itself.
        if (expected.StartsWith('"') && FractionDigits(expectedNumbers[0].Value) != FractionDigits(actualNumbers[0].Value))
            return false;

        var difference = decimal.Parse(expectedNumbers[0].Value, CultureInfo.InvariantCulture)
            - decimal.Parse(actualNumbers[0].Value, CultureInfo.InvariantCulture);
        return Math.Abs(difference) <= tolerance;
    }

    private static int FractionDigits(string number)
    {
        var point = number.IndexOf('.');
        return point < 0 ? 0 : number.Length - point - 1;
    }

    private static bool IsValidTimestamp(string value)
    {
        var trimmed = value.Trim('"');

        // Unix timestamp in milliseconds (13 digits)
        if (long.TryParse(trimmed, out var ts) && ts > 1_000_000_000_000L)
            return true;

        // Unix timestamp in seconds (10 digits)
        if (long.TryParse(trimmed, out var tsSec) && tsSec > 1_000_000_000L && tsSec < 10_000_000_000L)
            return true;

        // ISO 8601 date string
        if (DateTimeOffset.TryParse(trimmed, out _))
            return true;

        return false;
    }
}

/// <summary>
/// Result of comparing two responses
/// </summary>
public class ComparisonResult
{
    public string Context { get; set; } = "";
    public List<Difference> Differences { get; } = new();
    public bool IsMatch => Differences.Count == 0;

    public void AddDifference(string path, string? expected, string? actual)
    {
        Differences.Add(new Difference(path, expected, actual));
    }

    public override string ToString()
    {
        if (IsMatch)
            return $"{Context}: MATCH";

        var sb = new StringBuilder();
        sb.AppendLine($"{Context}: {Differences.Count} difference(s) found:");

        foreach (var diff in Differences.Take(20)) // Limit output for readability
        {
            sb.AppendLine($"  [{diff.Path}]");
            sb.AppendLine($"    Expected: {diff.Expected ?? "(null)"}");
            sb.AppendLine($"    Actual:   {diff.Actual ?? "(null)"}");
        }

        if (Differences.Count > 20)
        {
            sb.AppendLine($"  ... and {Differences.Count - 20} more differences");
        }

        return sb.ToString();
    }
}

/// <summary>
/// A single difference between expected and actual
/// </summary>
public record Difference(string Path, string? Expected, string? Actual);

/// <summary>
/// Rewrites the part of a string value matching <paramref name="Pattern"/> in the named fields
/// before comparison, for a value that is nondeterministic only in part.
/// </summary>
public record ValueNormaliser(Regex Pattern, string Replacement, IReadOnlySet<string> Fields);

/// <summary>
/// Options for response comparison
/// </summary>
public class ComparisonOptions
{
    /// <summary>
    /// Fields to skip entirely (generated server-side, will always differ)
    /// </summary>
    public HashSet<string> DynamicFields { get; init; } = new()
    {
        "srvCreated",
        "srvModified",
        "modifiedBy",
        "subject"
    };

    /// <summary>
    /// ID fields - verify presence but not exact value
    /// </summary>
    public HashSet<string> IdFields { get; init; } = new()
    {
        "_id",
        "identifier"
    };

    /// <summary>
    /// Timestamp fields - verify valid format but not exact value
    /// </summary>
    public HashSet<string> TimestampFields { get; init; } = new()
    {
        "date",
        "created_at",
        "mills",
        "srvDate",
        "serverTime",
        "dateString"
    };

    /// <summary>
    /// Partial rewrites applied to string values before comparison
    /// </summary>
    public IReadOnlyList<ValueNormaliser> ValueNormalisers { get; init; } = [];

    /// <summary>
    /// The body paths whose number may differ by the paired amount, a <c>*</c> segment standing for
    /// any one key. Nightscout serves the properties it computed when its last reload finished, and
    /// Nocturne computes them at the request. The parity wait reads at least a second after that
    /// reload started, by design (<see cref="Parity.ParityTestBase.WaitForNightscoutBodyAsync"/>),
    /// so the two are a second or two apart. A value derived from now therefore lands one tick
    /// apart: an IOB near a rounding boundary a hundredth, a displayed COB a tenth, and the minutes
    /// within the hour of a device age one minute, whenever a minute ticks in that lag. The
    /// unrounded COB moves by what the carbs absorb in that lag: 0.02 g is 3.6 seconds at the test
    /// profile's 20 g/h. The age, days and hours of a device change only at an hour boundary, which
    /// a lag of a second or two crosses about once in 2,000 tests, so they compare exactly. Every
    /// other path compares exactly.
    /// </summary>
    public static readonly IReadOnlyList<(string Path, decimal Tolerance)> ReloadLagToleranceTable =
    [
        ("Body.state.iob", 0.01m),
        ("Body.state.bwp", 0.01m),
        ("Body.iob.display", 0.01m),
        ("Body.iob.displayLine", 0.01m),
        ("Body.cob.cob", 0.02m),
        ("Body.cob.treatmentCOB.cob", 0.02m),
        ("Body.cob.display", 0.1m),
        ("Body.cob.displayLine", 0.1m),
        ("Body.cage.minFractions", 1m),
        ("Body.iage.minFractions", 1m),
        ("Body.bage.minFractions", 1m),
        ("Body.sage.*.minFractions", 1m)
    ];

    /// <summary>
    /// Body paths compared with a tolerance, empty unless <see cref="WithReloadLagTolerance"/>
    /// </summary>
    public IReadOnlyList<(string Path, decimal Tolerance)> ReloadLagTolerances { get; init; } = [];

    /// <summary>
    /// HTTP headers to compare
    /// </summary>
    public HashSet<string> HeadersToCompare { get; init; } = new()
    {
        "Content-Type"
    };

    /// <summary>
    /// When true, allows the actual (Nocturne) response to have extra fields
    /// that the expected (Nightscout) response doesn't have.
    /// This is useful because Nocturne often returns richer data than Nightscout.
    /// Default is true since Nocturne returning more data is acceptable.
    /// </summary>
    public bool AllowExtraActualFields { get; init; } = true;

    /// <summary>
    /// When true, only compares status codes for error responses (4xx/5xx).
    /// This is useful because Nightscout returns HTML error pages while ASP.NET returns JSON ProblemDetails.
    /// The important thing for API consumers is the status code, not the error body format.
    /// Default is true to focus on semantically meaningful parity.
    /// </summary>
    public bool StatusCodeOnlyForErrors { get; init; } = true;

    /// <summary>
    /// Default options suitable for most parity tests
    /// </summary>
    public static ComparisonOptions Default => new();

    /// <summary>
    /// Strict options - compare everything including timestamps, no extra fields allowed
    /// </summary>
    public static ComparisonOptions Strict => new()
    {
        DynamicFields = new HashSet<string>(),
        IdFields = new HashSet<string>(),
        TimestampFields = new HashSet<string>(),
        AllowExtraActualFields = false
    };

    public ComparisonOptions()
    {
    }

    private ComparisonOptions(ComparisonOptions source)
    {
        DynamicFields = new HashSet<string>(source.DynamicFields);
        IdFields = new HashSet<string>(source.IdFields);
        TimestampFields = new HashSet<string>(source.TimestampFields);
        HeadersToCompare = new HashSet<string>(source.HeadersToCompare);
        ValueNormalisers = source.ValueNormalisers;
        ReloadLagTolerances = source.ReloadLagTolerances;
        AllowExtraActualFields = source.AllowExtraActualFields;
        StatusCodeOnlyForErrors = source.StatusCodeOnlyForErrors;
    }

    /// <summary>
    /// Create options with additional fields whose presence is checked but whose value is not
    /// </summary>
    public ComparisonOptions WithPresenceOnlyFields(params string[] fields)
    {
        var options = new ComparisonOptions(this);
        options.IdFields.UnionWith(fields);
        return options;
    }

    /// <summary>
    /// Create options that compare the paths of <see cref="ReloadLagToleranceTable"/> to within their tolerance
    /// </summary>
    public ComparisonOptions WithReloadLagTolerance() => new(this) { ReloadLagTolerances = ReloadLagToleranceTable };

    /// <summary>
    /// Create options with additional fields to ignore
    /// </summary>
    public ComparisonOptions WithIgnoredFields(params string[] fields)
    {
        var options = new ComparisonOptions(this);
        options.DynamicFields.UnionWith(fields);
        return options;
    }

    /// <summary>
    /// Create options that require strict field matching (no extra fields allowed)
    /// </summary>
    public ComparisonOptions WithStrictFieldMatching() => new(this) { AllowExtraActualFields = false };

    /// <summary>
    /// Create options that rewrite what <paramref name="pattern"/> matches in the string values of
    /// <paramref name="fields"/> to <paramref name="replacement"/> before comparing them
    /// </summary>
    public ComparisonOptions WithNormalisedValues(Regex pattern, string replacement, params string[] fields) =>
        new(this) { ValueNormalisers = [.. ValueNormalisers, new ValueNormaliser(pattern, replacement, fields.ToHashSet())] };
}
