using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Models.Compatibility;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Compatibility;

/// <summary>
/// Compares the responses from Nightscout and Nocturne to identify compatibility discrepancies.
/// </summary>
/// <seealso cref="ResponseComparisonService"/>
public interface IResponseComparisonService
{
    /// <summary>
    /// Compares responses at status code, header, body, and performance levels.
    /// The <paramref name="requestPath"/> is used to look up route-specific field exclusions
    /// from <see cref="Configuration.ResponseComparisonSettings.RouteExcludeFields"/>.
    /// </summary>
    Task<ResponseComparisonResult> CompareResponsesAsync(
        TargetResponse? nightscoutResponse,
        TargetResponse? nocturneResponse,
        string traceId,
        string? requestPath = null
    );
}

/// <summary>
/// <see cref="IResponseComparisonService"/> implementation that performs structured JSON
/// comparison with configurable tolerances for numeric values, timestamps, and array ordering.
/// </summary>
/// <remarks>
/// <para>
/// JSON body comparison is recursive (<c>CompareJsonNodes</c>) and handles objects, arrays,
/// and scalar values. Numeric values within <see cref="Configuration.ResponseComparisonSettings.NumericPrecisionTolerance"/>
/// are treated as equal; timestamp fields (detected by path suffix heuristics) within
/// <see cref="Configuration.ResponseComparisonSettings.TimestampToleranceMs"/> are also treated as equal.
/// </para>
/// <para>
/// When <see cref="Configuration.ResponseComparisonSettings.AllowSupersetResponses"/> is
/// <see langword="true"/>, fields present only in the Nocturne response are silently ignored,
/// so Nocturne is allowed to return richer data without generating discrepancies.
/// </para>
/// <para>
/// Array comparison defaults to <see cref="Configuration.ArrayOrderHandling.Strict"/>
/// positional matching. The <c>Loose</c> mode currently falls back to strict (order-agnostic
/// matching is deferred). <c>Sorted</c> mode sorts elements by their string representation
/// before comparing.
/// </para>
/// <para>
/// Response bodies larger than 10 MB are compared as raw byte sequences; detailed field-level
/// analysis is skipped to avoid excessive memory allocation.
/// </para>
/// <para>
/// Performance differences greater than 1 000 ms are flagged as minor discrepancies.
/// </para>
/// </remarks>
/// <seealso cref="IResponseComparisonService"/>
public class ResponseComparisonService : IResponseComparisonService
{
    private readonly IOptions<CompatibilityProxyConfiguration> _configuration;
    private readonly ILogger<ResponseComparisonService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the ResponseComparisonService class
    /// </summary>
    /// <param name="configuration">Compatibility proxy configuration settings</param>
    /// <param name="logger">Logger instance for this service</param>
    public ResponseComparisonService(
        IOptions<CompatibilityProxyConfiguration> configuration,
        ILogger<ResponseComparisonService> logger
    )
    {
        _configuration = configuration;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };
    }

    /// <inheritdoc />
    public async Task<ResponseComparisonResult> CompareResponsesAsync(
        TargetResponse? nightscoutResponse,
        TargetResponse? nocturneResponse,
        string traceId,
        string? requestPath = null
    )
    {
        var result = new ResponseComparisonResult
        {
            TraceId = traceId,
            ComparisonTimestamp = DateTimeOffset.UtcNow,
        };

        // Sanitize user-controlled path to prevent log forging
        var sanitizedPath = requestPath?.Replace("\r", "").Replace("\n", "");

        try
        {
            _logger.LogDebug(
                "Starting response comparison for correlation {CorrelationId} on path {RequestPath}",
                traceId,
                sanitizedPath
            );

            // Check if both responses exist
            if (nightscoutResponse == null && nocturneResponse == null)
            {
                result.OverallMatch = Nocturne.Core.Models.ResponseMatchType.BothMissing;
                result.Summary = "Both responses are missing";
                return result;
            }

            if (nightscoutResponse == null)
            {
                result.OverallMatch = Nocturne.Core.Models.ResponseMatchType.NightscoutMissing;
                result.Summary = "Nightscout response is missing";
                return result;
            }

            if (nocturneResponse == null)
            {
                result.OverallMatch = Nocturne.Core.Models.ResponseMatchType.NocturneMissing;
                result.Summary = "Nocturne response is missing";
                return result;
            }

            // Compare status codes
            result.StatusCodeMatch = nightscoutResponse.StatusCode == nocturneResponse.StatusCode;
            if (!result.StatusCodeMatch)
            {
                result.Discrepancies.Add(
                    new ResponseDiscrepancy
                    {
                        Type = DiscrepancyType.StatusCode,
                        Severity = DiscrepancySeverity.Critical,
                        Field = "StatusCode",
                        NightscoutValue = nightscoutResponse.StatusCode.ToString(),
                        NocturneValue = nocturneResponse.StatusCode.ToString(),
                        Description =
                            $"Status code mismatch: Nightscout={nightscoutResponse.StatusCode}, Nocturne={nocturneResponse.StatusCode}",
                    }
                );
            }

            // Compare headers
            await CompareHeadersAsync(nightscoutResponse, nocturneResponse, result);

            // Compare response bodies
            await CompareResponseBodiesAsync(nightscoutResponse, nocturneResponse, result, requestPath);

            // Compare performance metrics
            ComparePerformanceMetrics(nightscoutResponse, nocturneResponse, result);

            // Determine overall match
            DetermineOverallMatch(result);

            _logger.LogDebug(
                "Response comparison completed for correlation {CorrelationId}. Match: {OverallMatch}, Discrepancies: {DiscrepancyCount}",
                traceId,
                result.OverallMatch,
                result.Discrepancies.Count
            );

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error comparing responses for correlation {CorrelationId}",
                traceId
            );
            result.OverallMatch = Nocturne.Core.Models.ResponseMatchType.ComparisonError;
            result.Summary = $"Comparison failed: {ex.Message}";
            return result;
        }
    }

    private async Task CompareHeadersAsync(
        TargetResponse nightscoutResponse,
        TargetResponse nocturneResponse,
        ResponseComparisonResult result
    )
    {
        var nightscoutHeaders = nightscoutResponse.Headers ?? new Dictionary<string, string[]>();
        var nocturneHeaders = nocturneResponse.Headers ?? new Dictionary<string, string[]>();

        // Compare content type
        var nightscoutContentType = nightscoutResponse.ContentType ?? "";
        var nocturneContentType = nocturneResponse.ContentType ?? "";

        if (
            !string.Equals(
                nightscoutContentType,
                nocturneContentType,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            result.Discrepancies.Add(
                new ResponseDiscrepancy
                {
                    Type = DiscrepancyType.ContentType,
                    Severity = DiscrepancySeverity.Minor,
                    Field = "ContentType",
                    NightscoutValue = nightscoutContentType,
                    NocturneValue = nocturneContentType,
                    Description = "Content type mismatch",
                }
            );
        }

        // Compare important headers (excluding sensitive and variable ones)
        var importantHeaders = new[] { "cache-control", "content-encoding", "transfer-encoding" };

        foreach (var headerName in importantHeaders)
        {
            var nightscoutValue =
                nightscoutHeaders.GetValueOrDefault(headerName)?.FirstOrDefault() ?? "";
            var nocturneValue =
                nocturneHeaders.GetValueOrDefault(headerName)?.FirstOrDefault() ?? "";

            if (!string.Equals(nightscoutValue, nocturneValue, StringComparison.OrdinalIgnoreCase))
            {
                result.Discrepancies.Add(
                    new ResponseDiscrepancy
                    {
                        Type = DiscrepancyType.Header,
                        Severity = DiscrepancySeverity.Minor,
                        Field = headerName,
                        NightscoutValue = nightscoutValue,
                        NocturneValue = nocturneValue,
                        Description = $"Header '{headerName}' mismatch",
                    }
                );
            }
        }

        await Task.CompletedTask; // Placeholder for potential async header processing
    }

    private async Task CompareResponseBodiesAsync(
        TargetResponse nightscoutResponse,
        TargetResponse nocturneResponse,
        ResponseComparisonResult result,
        string? requestPath
    )
    {
        var nightscoutBody = nightscoutResponse.Body;
        var nocturneBody = nocturneResponse.Body;

        // If both are null or empty, they match
        if (
            (nightscoutBody == null || nightscoutBody.Length == 0)
            && (nocturneBody == null || nocturneBody.Length == 0)
        )
        {
            result.BodyMatch = true;
            return;
        }

        // If one is null/empty and the other isn't, they don't match
        if (
            (nightscoutBody == null || nightscoutBody.Length == 0)
            || (nocturneBody == null || nocturneBody.Length == 0)
        )
        {
            result.BodyMatch = false;
            result.Discrepancies.Add(
                new ResponseDiscrepancy
                {
                    Type = DiscrepancyType.Body,
                    Severity = DiscrepancySeverity.Critical,
                    Field = "Body",
                    NightscoutValue = nightscoutBody?.Length.ToString() ?? "0",
                    NocturneValue = nocturneBody?.Length.ToString() ?? "0",
                    Description = "Response body presence mismatch",
                }
            );
            return;
        }

        // Check if response is too large for detailed comparison
        const long maxSize = 10 * 1024 * 1024; // 10MB
        if (nightscoutBody.Length > maxSize || nocturneBody.Length > maxSize)
        {
            result.BodyMatch = nightscoutBody.SequenceEqual(nocturneBody);
            if (!result.BodyMatch)
            {
                result.Discrepancies.Add(
                    new ResponseDiscrepancy
                    {
                        Type = DiscrepancyType.Body,
                        Severity = DiscrepancySeverity.Critical,
                        Field = "Body",
                        NightscoutValue = $"{nightscoutBody.Length} bytes",
                        NocturneValue = $"{nocturneBody.Length} bytes",
                        Description = "Large response bodies differ (detailed comparison skipped)",
                    }
                );
            }
            return;
        }

        try
        {
            var nightscoutText = System.Text.Encoding.UTF8.GetString(nightscoutBody);
            var nocturneText = System.Text.Encoding.UTF8.GetString(nocturneBody);

            // Try to parse as JSON for detailed comparison
            if (
                IsJsonContent(nightscoutResponse.ContentType)
                && IsJsonContent(nocturneResponse.ContentType)
            )
            {
                await CompareJsonBodiesAsync(nightscoutText, nocturneText, result, requestPath);
            }
            else
            {
                // Simple text comparison
                result.BodyMatch = string.Equals(
                    nightscoutText,
                    nocturneText,
                    StringComparison.Ordinal
                );
                if (!result.BodyMatch)
                {
                    result.Discrepancies.Add(
                        new ResponseDiscrepancy
                        {
                            Type = DiscrepancyType.Body,
                            Severity = DiscrepancySeverity.Critical,
                            Field = "Body",
                            NightscoutValue = TruncateForLogging(nightscoutText),
                            NocturneValue = TruncateForLogging(nocturneText),
                            Description = "Response body text differs",
                        }
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing response bodies for comparison");
            result.BodyMatch = nightscoutBody.SequenceEqual(nocturneBody);
            if (!result.BodyMatch)
            {
                result.Discrepancies.Add(
                    new ResponseDiscrepancy
                    {
                        Type = DiscrepancyType.Body,
                        Severity = DiscrepancySeverity.Critical,
                        Field = "Body",
                        NightscoutValue = $"{nightscoutBody.Length} bytes",
                        NocturneValue = $"{nocturneBody.Length} bytes",
                        Description = "Binary response bodies differ",
                    }
                );
            }
        }
    }

    private async Task CompareJsonBodiesAsync(
        string nightscoutJson,
        string nocturneJson,
        ResponseComparisonResult result,
        string? requestPath
    )
    {
        try
        {
            var nightscoutNode = JsonNode.Parse(nightscoutJson);
            var nocturneNode = JsonNode.Parse(nocturneJson);

            var comparisonSettings = _configuration.Value.Comparison;

            // Build merged exclusion list (global + route-specific)
            var mergedExcludeFields = new List<string>(comparisonSettings.ExcludeFields);
            if (!string.IsNullOrEmpty(requestPath) && comparisonSettings.RouteExcludeFields != null)
            {
                // Find matching route patterns
                foreach (var routePattern in comparisonSettings.RouteExcludeFields.Keys)
                {
                    if (requestPath.StartsWith(routePattern, StringComparison.OrdinalIgnoreCase))
                    {
                        mergedExcludeFields.AddRange(comparisonSettings.RouteExcludeFields[routePattern]);
                    }
                }
            }

            // Create a modified settings object with merged exclusions
            var effectiveSettings = new ResponseComparisonSettings
            {
                ExcludeFields = mergedExcludeFields,
                RouteExcludeFields = comparisonSettings.RouteExcludeFields ?? new Dictionary<string, List<string>>(),
                AllowSupersetResponses = comparisonSettings.AllowSupersetResponses,
                TimestampToleranceMs = comparisonSettings.TimestampToleranceMs,
                NumericPrecisionTolerance = comparisonSettings.NumericPrecisionTolerance,
                NormalizeFieldOrdering = comparisonSettings.NormalizeFieldOrdering,
                ArrayOrderHandling = comparisonSettings.ArrayOrderHandling,
                EnableDeepComparison = comparisonSettings.EnableDeepComparison,
            };

            var differences = CompareJsonNodes(
                nightscoutNode,
                nocturneNode,
                "",
                effectiveSettings
            );

            result.BodyMatch = differences.Count == 0;
            result.Discrepancies.AddRange(differences);

            await Task.CompletedTask; // Placeholder for potential async JSON processing
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Error parsing JSON for detailed comparison");
            result.BodyMatch = string.Equals(
                nightscoutJson,
                nocturneJson,
                StringComparison.Ordinal
            );
            if (!result.BodyMatch)
            {
                result.Discrepancies.Add(
                    new ResponseDiscrepancy
                    {
                        Type = DiscrepancyType.Body,
                        Severity = DiscrepancySeverity.Critical,
                        Field = "Body",
                        NightscoutValue = TruncateForLogging(nightscoutJson),
                        NocturneValue = TruncateForLogging(nocturneJson),
                        Description = "JSON responses differ (detailed comparison failed)",
                    }
                );
            }
        }
    }

    private List<ResponseDiscrepancy> CompareJsonNodes(
        JsonNode? nightscoutNode,
        JsonNode? nocturneNode,
        string path,
        ResponseComparisonSettings settings
    )
    {
        var differences = new List<ResponseDiscrepancy>();

        if (nightscoutNode == null && nocturneNode == null)
            return differences;

        if (nightscoutNode == null || nocturneNode == null)
        {
            differences.Add(
                new ResponseDiscrepancy
                {
                    Type = DiscrepancyType.JsonStructure,
                    Severity = DiscrepancySeverity.Critical,
                    Field = path,
                    NightscoutValue = nightscoutNode?.ToString() ?? "null",
                    NocturneValue = nocturneNode?.ToString() ?? "null",
                    Description = "JSON structure mismatch - one side is null",
                }
            );
            return differences;
        }

        // Compare different node types
        if (nightscoutNode.GetType() != nocturneNode.GetType())
        {
            differences.Add(
                new ResponseDiscrepancy
                {
                    Type = DiscrepancyType.JsonStructure,
                    Severity = DiscrepancySeverity.Critical,
                    Field = path,
                    NightscoutValue = nightscoutNode.GetType().Name,
                    NocturneValue = nocturneNode.GetType().Name,
                    Description = "JSON node type mismatch",
                }
            );
            return differences;
        }

        // Handle different node types
        switch (nightscoutNode)
        {
            case JsonObject nightscoutObj when nocturneNode is JsonObject nocturneObj:
                differences.AddRange(
                    CompareJsonObjects(nightscoutObj, nocturneObj, path, settings)
                );
                break;
            case JsonArray nightscoutArray when nocturneNode is JsonArray nocturneArray:
                differences.AddRange(
                    CompareJsonArrays(nightscoutArray, nocturneArray, path, settings)
                );
                break;
            case JsonValue nightscoutValue when nocturneNode is JsonValue nocturneValue:
                differences.AddRange(
                    CompareJsonValues(nightscoutValue, nocturneValue, path, settings)
                );
                break;
        }

        return differences;
    }

    private List<ResponseDiscrepancy> CompareJsonObjects(
        JsonObject nightscoutObj,
        JsonObject nocturneObj,
        string path,
        ResponseComparisonSettings settings
    )
    {
        var differences = new List<ResponseDiscrepancy>();

        // Get all keys based on superset setting
        HashSet<string> keysToCompare;
        if (settings.AllowSupersetResponses)
        {
            // Only check keys that exist in Nightscout (Nocturne can have extra fields)
            keysToCompare = nightscoutObj.Select(kv => kv.Key).ToHashSet();
        }
        else
        {
            // Check all keys from both responses
            keysToCompare = nightscoutObj
                .Select(kv => kv.Key)
                .Union(nocturneObj.Select(kv => kv.Key))
                .ToHashSet();
        }

        foreach (var key in keysToCompare)
        {
            var fieldPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";

            // Skip excluded fields
            if (settings.ExcludeFields.Contains(key))
                continue;

            var nightscoutHasKey = nightscoutObj.TryGetPropertyValue(key, out var nsValue);
            var nocturneHasKey = nocturneObj.TryGetPropertyValue(key, out var nValue);

            var nightscoutValue = nightscoutHasKey ? nsValue : null;
            var nocturneValue = nocturneHasKey ? nValue : null;

            // If superset is allowed and key only exists in Nocturne, skip it
            if (settings.AllowSupersetResponses && !nightscoutHasKey && nocturneHasKey)
            {
                continue;
            }

            differences.AddRange(
                CompareJsonNodes(nightscoutValue, nocturneValue, fieldPath, settings)
            );
        }

        return differences;
    }

    private List<ResponseDiscrepancy> CompareJsonArrays(
        JsonArray nightscoutArray,
        JsonArray nocturneArray,
        string path,
        ResponseComparisonSettings settings
    )
    {
        var differences = new List<ResponseDiscrepancy>();

        if (nightscoutArray.Count != nocturneArray.Count)
        {
            differences.Add(
                new ResponseDiscrepancy
                {
                    Type = DiscrepancyType.ArrayLength,
                    Severity = DiscrepancySeverity.Major,
                    Field = path,
                    NightscoutValue = nightscoutArray.Count.ToString(),
                    NocturneValue = nocturneArray.Count.ToString(),
                    Description = "Array length mismatch",
                }
            );
        }

        // Handle array comparison based on settings
        switch (settings.ArrayOrderHandling)
        {
            case ArrayOrderHandling.Strict:
                for (int i = 0; i < Math.Min(nightscoutArray.Count, nocturneArray.Count); i++)
                {
                    differences.AddRange(
                        CompareJsonNodes(
                            nightscoutArray[i],
                            nocturneArray[i],
                            $"{path}[{i}]",
                            settings
                        )
                    );
                }
                break;

            case ArrayOrderHandling.Loose:
                // This would require more complex matching logic
                // For now, fall back to strict comparison
                goto case ArrayOrderHandling.Strict;

            case ArrayOrderHandling.Sorted:
                // Sort arrays by their string representation for comparison
                var sortedNightscout = nightscoutArray.OrderBy(n => n?.ToString()).ToArray();
                var sortedNocturne = nocturneArray.OrderBy(n => n?.ToString()).ToArray();

                for (int i = 0; i < Math.Min(sortedNightscout.Length, sortedNocturne.Length); i++)
                {
                    differences.AddRange(
                        CompareJsonNodes(
                            sortedNightscout[i],
                            sortedNocturne[i],
                            $"{path}[sorted-{i}]",
                            settings
                        )
                    );
                }
                break;
        }

        return differences;
    }

    private List<ResponseDiscrepancy> CompareJsonValues(
        JsonValue nightscoutValue,
        JsonValue nocturneValue,
        string path,
        ResponseComparisonSettings settings
    )
    {
        var differences = new List<ResponseDiscrepancy>();

        var nightscoutString = nightscoutValue.ToString();
        var nocturneString = nocturneValue.ToString();

        // Handle numeric comparisons with tolerance
        if (
            nightscoutValue.TryGetValue<double>(out var nsDouble)
            && nocturneValue.TryGetValue<double>(out var nDouble)
        )
        {
            if (Math.Abs(nsDouble - nDouble) > settings.NumericPrecisionTolerance)
            {
                differences.Add(
                    new ResponseDiscrepancy
                    {
                        Type = DiscrepancyType.NumericValue,
                        Severity = DiscrepancySeverity.Minor,
                        Field = path,
                        NightscoutValue = nsDouble.ToString(),
                        NocturneValue = nDouble.ToString(),
                        Description =
                            $"Numeric value differs beyond tolerance ({settings.NumericPrecisionTolerance})",
                    }
                );
            }
        }
        // Handle timestamp comparisons with tolerance
        else if (
            IsTimestampField(path)
            && DateTime.TryParse(nightscoutString, out var nsTime)
            && DateTime.TryParse(nocturneString, out var nTime)
        )
        {
            if (Math.Abs((nsTime - nTime).TotalMilliseconds) > settings.TimestampToleranceMs)
            {
                differences.Add(
                    new ResponseDiscrepancy
                    {
                        Type = DiscrepancyType.Timestamp,
                        Severity = DiscrepancySeverity.Minor,
                        Field = path,
                        NightscoutValue = nightscoutString,
                        NocturneValue = nocturneString,
                        Description =
                            $"Timestamp differs beyond tolerance ({settings.TimestampToleranceMs}ms)",
                    }
                );
            }
        }
        // Standard string comparison
        else if (!string.Equals(nightscoutString, nocturneString, StringComparison.Ordinal))
        {
            differences.Add(
                new ResponseDiscrepancy
                {
                    Type = DiscrepancyType.StringValue,
                    Severity = DiscrepancySeverity.Major,
                    Field = path,
                    NightscoutValue = nightscoutString,
                    NocturneValue = nocturneString,
                    Description = "String value differs",
                }
            );
        }

        return differences;
    }

    private void ComparePerformanceMetrics(
        TargetResponse nightscoutResponse,
        TargetResponse nocturneResponse,
        ResponseComparisonResult result
    )
    {
        result.PerformanceComparison = new PerformanceComparison
        {
            NightscoutResponseTime = nightscoutResponse.ResponseTimeMs,
            NocturneResponseTime = nocturneResponse.ResponseTimeMs,
            TimeDifference = Math.Abs(
                nightscoutResponse.ResponseTimeMs - nocturneResponse.ResponseTimeMs
            ),
            FasterSystem =
                nightscoutResponse.ResponseTimeMs <= nocturneResponse.ResponseTimeMs
                    ? "Nightscout"
                    : "Nocturne",
        };

        // Flag significant performance differences
        if (result.PerformanceComparison.TimeDifference > 1000) // 1 second
        {
            result.Discrepancies.Add(
                new ResponseDiscrepancy
                {
                    Type = DiscrepancyType.Performance,
                    Severity = DiscrepancySeverity.Minor,
                    Field = "ResponseTime",
                    NightscoutValue = $"{nightscoutResponse.ResponseTimeMs}ms",
                    NocturneValue = $"{nocturneResponse.ResponseTimeMs}ms",
                    Description =
                        $"Significant performance difference: {result.PerformanceComparison.TimeDifference}ms",
                }
            );
        }
    }

    private void DetermineOverallMatch(ResponseComparisonResult result)
    {
        var criticalCount = result.Discrepancies.Count(d =>
            d.Severity == DiscrepancySeverity.Critical
        );
        var majorCount = result.Discrepancies.Count(d => d.Severity == DiscrepancySeverity.Major);
        var minorCount = result.Discrepancies.Count(d => d.Severity == DiscrepancySeverity.Minor);

        if (criticalCount > 0)
        {
            result.OverallMatch = Nocturne.Core.Models.ResponseMatchType.CriticalDifferences;
            result.Summary =
                $"Critical differences found: {criticalCount} critical, {majorCount} major, {minorCount} minor";
        }
        else if (majorCount > 0)
        {
            result.OverallMatch = Nocturne.Core.Models.ResponseMatchType.MajorDifferences;
            result.Summary = $"Major differences found: {majorCount} major, {minorCount} minor";
        }
        else if (minorCount > 0)
        {
            result.OverallMatch = Nocturne.Core.Models.ResponseMatchType.MinorDifferences;
            result.Summary = $"Minor differences found: {minorCount} minor";
        }
        else
        {
            result.OverallMatch = Nocturne.Core.Models.ResponseMatchType.Perfect;
            result.Summary = "Responses match perfectly";
        }
    }

    private static bool IsJsonContent(string? contentType) =>
        !string.IsNullOrEmpty(contentType)
        && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static bool IsTimestampField(string fieldPath) =>
        fieldPath.Contains("time", StringComparison.OrdinalIgnoreCase)
        || fieldPath.Contains("date", StringComparison.OrdinalIgnoreCase)
        || fieldPath.EndsWith("_at", StringComparison.OrdinalIgnoreCase);

    private string TruncateForLogging(string text, int maxLength = 200)
    {
        if (text.Length <= maxLength)
            return text;

        return text.Substring(0, maxLength) + "...";
    }
}
