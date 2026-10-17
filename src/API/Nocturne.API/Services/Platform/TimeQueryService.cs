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
        string? find = null,
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

        var timePatterns = _braceExpansionService.PrepareTimePatterns(prefix, regex, fieldName);
        var findQuery = BuildFind(find, fieldName, timePatterns, type: null);

        return storage.ToLowerInvariant() switch
        {
            "entries" => await _entries.GetEntriesWithAdvancedFilterAsync(
                findQuery,
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
        string? find = null,
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

        var timePatterns = _braceExpansionService.PrepareTimePatterns(prefix, regex, field);
        var findQuery = BuildFind(find, field, timePatterns, type);

        return storage.ToLowerInvariant() switch
        {
            "entries" => await _entries.GetEntriesWithAdvancedFilterAsync(
                findQuery,
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
    /// Merges the time pattern and slice type with the caller's <c>find[...]</c> keys into one
    /// querystring-form find, the shape every read service parses. A single prefix is ANDed with
    /// the expanded patterns, as upstream does. Expanded patterns become one alternation because
    /// <c>$in</c> compares literally. Repeated <c>$regex</c> keys on one field stay separate
    /// conditions in <see cref="Nocturne.Core.Models.Queries.FindQuery"/>.
    /// </summary>
    private static string BuildFind(
        string? find,
        string field,
        Nocturne.Core.Contracts.Legacy.TimePatternQuery timePatterns,
        string? type
    )
    {
        var patterns = new List<string>();
        if (timePatterns.CanOptimizeWithIndex && !string.IsNullOrEmpty(timePatterns.SingleRegexPattern))
            patterns.Add(timePatterns.SingleRegexPattern);

        var expanded = (timePatterns.InPatterns ?? []).Where(p => p.Length > 0).ToArray();
        if (expanded.Length > 0)
            patterns.Add(string.Join('|', expanded));

        var terms = patterns
            .Select(p => $"find[{field}][$regex]={Uri.EscapeDataString(p)}")
            .ToList();
        if (!string.IsNullOrEmpty(type))
            terms.Add($"find[type]={Uri.EscapeDataString(type)}");
        if (!string.IsNullOrEmpty(find))
            terms.Add(find);

        return string.Join('&', terms);
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
