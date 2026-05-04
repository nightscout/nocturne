using System.Text.Json;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.API.Services.Devices;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// Implements time-based queries and advanced data slicing for the Nightscout
/// <c>/api/v1/slice</c> endpoint. Supports brace-expanded time-pattern matching on entries,
/// treatments, and device statuses, replicating the legacy JavaScript functionality.
/// </summary>
/// <seealso cref="ITimeQueryService"/>
/// <seealso cref="IBraceExpansionService"/>
public class TimeQueryService : ITimeQueryService
{
    private static readonly string[] OperatorSuffixes = ["_gte", "_lte", "_gt", "_lt", "_ne", "_regex", "_in", "_nin"];

    private readonly IEntryService _entries;
    private readonly ITreatmentService _treatments;
    private readonly DeviceStatusProjectionService _deviceStatuses;
    private readonly IBraceExpansionService _braceExpansionService;
    private readonly ILogger<TimeQueryService> _logger;

    public TimeQueryService(
        IEntryService entries,
        ITreatmentService treatments,
        DeviceStatusProjectionService deviceStatuses,
        IBraceExpansionService braceExpansionService,
        ILogger<TimeQueryService> logger
    )
    {
        _entries = entries;
        _treatments = treatments;
        _deviceStatuses = deviceStatuses;
        _braceExpansionService = braceExpansionService;
        _logger = logger;
    }

    /// <summary>
    /// Execute a time-based query with pattern matching
    /// </summary>
    public async Task<IEnumerable<Entry>> ExecuteTimeQueryAsync(
        string? prefix,
        string? regex,
        string storage = "entries",
        string fieldName = "dateString",
        Dictionary<string, object>? queryParameters = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Executing time query - prefix: {Prefix}, regex: {Regex}, storage: {Storage}, field: {Field}",
            prefix,
            regex,
            storage,
            fieldName
        );

        // Prepare time patterns for the specified field
        var timePatterns = _braceExpansionService.PrepareTimePatterns(prefix, regex, fieldName);

        // Build query parameters for PostgreSQL
        var queryParams = new Dictionary<string, object>();

        // Add time pattern filtering
        if (
            timePatterns.CanOptimizeWithIndex
            && !string.IsNullOrEmpty(timePatterns.SingleRegexPattern)
        )
        {
            queryParams[fieldName + "_regex"] = timePatterns.SingleRegexPattern;
        }
        else if (timePatterns.InPatterns?.Any() == true)
        {
            queryParams[fieldName + "_in"] = timePatterns.InPatterns;
        }

        // Apply additional query parameters
        if (queryParameters != null)
        {
            foreach (var kvp in queryParameters)
            {
                // Handle special MongoDB-style query operators
                if (kvp.Key == "find" && kvp.Value is Dictionary<string, object> findDict)
                {
                    foreach (var findKvp in findDict)
                    {
                        if (findKvp.Value is Dictionary<string, object> operatorDict)
                        {
                            // Handle MongoDB operators like $in, $nin, $regex, etc.
                            foreach (var opKvp in operatorDict)
                            {
                                switch (opKvp.Key)
                                {
                                    case "$in":
                                        queryParams[findKvp.Key + "_in"] = ConvertArray(
                                            opKvp.Value
                                        );
                                        break;
                                    case "$nin":
                                        queryParams[findKvp.Key + "_nin"] = ConvertArray(
                                            opKvp.Value
                                        );
                                        break;
                                    case "$regex":
                                        queryParams[findKvp.Key + "_regex"] = ConvertValue(
                                            opKvp.Value
                                        );
                                        break;
                                    case "$gte":
                                        queryParams[findKvp.Key + "_gte"] = ConvertValue(
                                            opKvp.Value
                                        );
                                        break;
                                    case "$lte":
                                        queryParams[findKvp.Key + "_lte"] = ConvertValue(
                                            opKvp.Value
                                        );
                                        break;
                                    case "$gt":
                                        queryParams[findKvp.Key + "_gt"] = ConvertValue(
                                            opKvp.Value
                                        );
                                        break;
                                    case "$lt":
                                        queryParams[findKvp.Key + "_lt"] = ConvertValue(
                                            opKvp.Value
                                        );
                                        break;
                                    case "$ne":
                                        queryParams[findKvp.Key + "_ne"] = ConvertValue(
                                            opKvp.Value
                                        );
                                        break;
                                    default:
                                        queryParams[findKvp.Key] = ConvertValue(opKvp.Value);
                                        break;
                                }
                            }
                        }
                        else
                        {
                            queryParams[findKvp.Key] = ConvertValue(findKvp.Value);
                        }
                    }
                }
                else if (kvp.Key != "count" && kvp.Key != "sort")
                {
                    queryParams[kvp.Key] = ConvertValue(kvp.Value);
                }
            }
        }

        // Execute query based on storage type
        var findQuery = ConvertQueryParamsToFindQuery(queryParams);

        return storage.ToLowerInvariant() switch
        {
            "entries" => await _entries.GetEntriesWithAdvancedFilterAsync(
                findQuery ?? "{}",
                count: 1000,
                skip: 0,
                cancellationToken: cancellationToken
            ),
            "treatments" => (
                await _treatments.GetTreatmentsWithAdvancedFilterAsync(
                    count: 1000,
                    skip: 0,
                    findQuery: findQuery,
                    reverseResults: false,
                    cancellationToken: cancellationToken
                )
            ).Select(t => ConvertTreatmentToEntry(t)),
            "devicestatus" => (
                await _deviceStatuses.GetAsync(
                    count: 1000,
                    skip: 0,
                    find: findQuery,
                    ct: cancellationToken
                )
            ).Select(ds => ConvertDeviceStatusToEntry(ds)),
            _ => throw new ArgumentException($"Unsupported storage type: {storage}"),
        };
    }

    /// <summary>
    /// Execute an advanced slice query with field and type filtering
    /// </summary>
    public async Task<IEnumerable<Entry>> ExecuteSliceQueryAsync(
        string storage,
        string field,
        string? type,
        string? prefix,
        string? regex,
        Dictionary<string, object>? queryParameters = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Executing slice query - storage: {Storage}, field: {Field}, type: {Type}, prefix: {Prefix}, regex: {Regex}",
            storage,
            field,
            type,
            prefix,
            regex
        );

        // Prepare time patterns for the specified field
        var timePatterns = _braceExpansionService.PrepareTimePatterns(prefix, regex, field);

        // Build query parameters for PostgreSQL
        var queryParams = new Dictionary<string, object>();

        // Add time pattern filtering
        if (
            timePatterns.CanOptimizeWithIndex
            && !string.IsNullOrEmpty(timePatterns.SingleRegexPattern)
        )
        {
            queryParams[field + "_regex"] = timePatterns.SingleRegexPattern;
        }
        else
        {
            queryParams[field + "_in"] = timePatterns.InPatterns;
        }

        // Apply type filter if specified
        if (!string.IsNullOrEmpty(type))
        {
            queryParams["type"] = type;
        }

        // Apply additional query parameters
        if (queryParameters != null)
        {
            foreach (var kvp in queryParameters)
            {
                queryParams[kvp.Key] = kvp.Value;
            }
        }

        // Execute query based on storage type
        // Note: PostgreSQL service uses different method signatures than MongoDB
        // Converting parameters to match PostgreSQL API
        var findQuery = ConvertQueryParamsToFindQuery(queryParams);

        return storage.ToLowerInvariant() switch
        {
            "entries" => await _entries.GetEntriesWithAdvancedFilterAsync(
                findQuery ?? "{}",
                count: 1000,
                skip: 0,
                cancellationToken: cancellationToken
            ),
            "treatments" => (
                await _treatments.GetTreatmentsWithAdvancedFilterAsync(
                    count: 1000,
                    skip: 0,
                    findQuery: findQuery,
                    reverseResults: false,
                    cancellationToken: cancellationToken
                )
            ).Select(t => ConvertTreatmentToEntry(t)),
            "devicestatus" => (
                await _deviceStatuses.GetAsync(
                    count: 1000,
                    skip: 0,
                    find: findQuery,
                    ct: cancellationToken
                )
            ).Select(ds => ConvertDeviceStatusToEntry(ds)),
            _ => throw new ArgumentException($"Unsupported storage type: {storage}"),
        };
    }

    /// <summary>
    /// Generate debug information for time pattern queries
    /// </summary>
    public TimeQueryEcho GenerateTimeQueryEcho(
        string? prefix,
        string? regex,
        string storage = "entries",
        string fieldName = "dateString",
        Dictionary<string, object>? queryParameters = null
    )
    {
        _logger.LogDebug(
            "Generating time query echo - prefix: {Prefix}, regex: {Regex}, storage: {Storage}, field: {Field}",
            prefix,
            regex,
            storage,
            fieldName
        );

        // Prepare time patterns
        var timePatterns = _braceExpansionService.PrepareTimePatterns(prefix, regex, fieldName);

        // Build the query structure that matches legacy MongoDB format for compatibility
        var queryStructure = new Dictionary<string, object>();

        if (
            timePatterns.CanOptimizeWithIndex
            && !string.IsNullOrEmpty(timePatterns.SingleRegexPattern)
        )
        {
            // Use MongoDB-style $regex operator for compatibility
            queryStructure[fieldName] = new Dictionary<string, object>
            {
                ["$regex"] = timePatterns.SingleRegexPattern,
            };
        }
        else if (timePatterns.InPatterns?.Any() == true)
        {
            // Use MongoDB-style $in operator for compatibility
            queryStructure[fieldName] = new Dictionary<string, object>
            {
                ["$in"] = timePatterns.InPatterns,
            };
        }

        // Add any additional query parameters
        if (queryParameters != null)
        {
            foreach (var kvp in queryParameters)
            {
                if (kvp.Key != "find" && kvp.Key != "count" && kvp.Key != "sort")
                {
                    queryStructure[kvp.Key] = kvp.Value;
                }
            }

            // Handle find parameters
            if (
                queryParameters.TryGetValue("find", out var findValue)
                && findValue is Dictionary<string, object> findDict
            )
            {
                foreach (var findKvp in findDict)
                {
                    queryStructure[findKvp.Key] = findKvp.Value;
                }
            }
        }

        return new TimeQueryEcho
        {
            Req = new TimeQueryRequest
            {
                Params = new Dictionary<string, string?>
                {
                    ["prefix"] = prefix,
                    ["regex"] = regex,
                    ["storage"] = storage,
                    ["field"] = fieldName,
                },
                Query = queryParameters ?? new Dictionary<string, object>(),
            },
            Pattern = timePatterns.Patterns,
            Query = queryStructure,
        };
    }

    /// <summary>
    /// Convert a value to the appropriate type for MongoDB queries
    /// </summary>
    private object ConvertValue(object value)
    {
        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.Number => jsonElement.TryGetInt64(out var longVal)
                    ? longVal
                    : jsonElement.GetDouble(),
                JsonValueKind.String => jsonElement.GetString() ?? "",
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => value.ToString() ?? "",
            };
        }

        return value;
    }

    /// <summary>
    /// Convert a value to an array for MongoDB $in/$nin operations
    /// </summary>
    private IEnumerable<object> ConvertArray(object value)
    {
        if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
        {
            return jsonElement.EnumerateArray().Select(element => ConvertValue(element)).ToArray();
        }

        if (value is IEnumerable<object> enumerable)
        {
            return enumerable.Select(item => ConvertValue(item)).ToArray();
        }

        return new[] { ConvertValue(value) };
    }

    /// <summary>
    /// Convert query parameters dictionary to standard MongoDB JSON format.
    /// Translates field_operator keys (e.g. dateString_regex) into
    /// <c>{"field": {"$operator": value}}</c> for downstream find-query parsing.
    /// </summary>
    private string? ConvertQueryParamsToFindQuery(Dictionary<string, object> queryParams)
    {
        if (queryParams.Count == 0)
        {
            return null;
        }

        // Convert field_operator format to MongoDB JSON: {"field": {"$operator": value}}
        var mongoDoc = new Dictionary<string, object>();

        foreach (var kvp in queryParams)
        {
            var key = kvp.Key;
            var value = kvp.Value;

            // Check for operator suffix pattern: field_gte, field_lte, field_regex, etc.
            var matchedSuffix = OperatorSuffixes.FirstOrDefault(s => key.EndsWith(s));

            if (matchedSuffix != null)
            {
                var fieldName = key[..^matchedSuffix.Length];
                var mongoOp = "$" + matchedSuffix[1..]; // _gte → $gte

                if (!mongoDoc.TryGetValue(fieldName, out var existing) || existing is not Dictionary<string, object> ops)
                {
                    ops = new Dictionary<string, object>();
                    mongoDoc[fieldName] = ops;
                }
                ops[mongoOp] = value;
            }
            else
            {
                mongoDoc[key] = value;
            }
        }

        return JsonSerializer.Serialize(mongoDoc);
    }

    /// <summary>
    /// Convert Treatment to Entry for unified result handling
    /// </summary>
    private Entry ConvertTreatmentToEntry(Treatment treatment)
    {
        return new Entry
        {
            Id = treatment.Id,
            Type = "treatment",
            Mills = treatment.Mills,
            CreatedAt = treatment.CreatedAt,
            UtcOffset = treatment.UtcOffset,
            // Map other relevant fields as needed
        };
    }

    /// <summary>
    /// Convert DeviceStatus to Entry for unified result handling
    /// </summary>
    private Entry ConvertDeviceStatusToEntry(DeviceStatus deviceStatus)
    {
        return new Entry
        {
            Id = deviceStatus.Id,
            Type = "devicestatus",
            Mills = deviceStatus.Mills,
            CreatedAt = deviceStatus.CreatedAt,
            UtcOffset = deviceStatus.UtcOffset,
            Device = deviceStatus.Device,
            // Map other relevant fields as needed
        };
    }
}
