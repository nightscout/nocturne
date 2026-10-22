using Microsoft.AspNetCore.WebUtilities;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
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
            "entries" => await _entries.QueryEntriesAsync(
                new EntryQuery { Count = 1000, Find = findQuery },
                cancellationToken
            ),
            "treatments" => (
                await _treatments.QueryTreatmentsAsync(
                    new TreatmentQuery { Count = 1000, Find = findQuery },
                    cancellationToken
                )
            ).Select(t => ConvertTreatmentToEntry(t)),
            "devicestatus" => (
                await _deviceStatuses.GetMatchingAsync(
                    DeviceStatusProjectionService.ParseFind(findQuery),
                    count: 1000,
                    skip: 0,
                    cancellationToken
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
            "entries" => await _entries.QueryEntriesAsync(
                new EntryQuery { Count = 1000, Find = findQuery },
                cancellationToken
            ),
            "treatments" => (
                await _treatments.QueryTreatmentsAsync(
                    new TreatmentQuery { Count = 1000, Find = findQuery },
                    cancellationToken
                )
            ).Select(t => ConvertTreatmentToEntry(t)),
            "devicestatus" => (
                await _deviceStatuses.GetMatchingAsync(
                    DeviceStatusProjectionService.ParseFind(findQuery),
                    count: 1000,
                    skip: 0,
                    cancellationToken
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
        string? queryString = null
    ) {
        _logger.LogDebug(
            "Generating time query echo - prefix: {Prefix}, regex: {Regex}, storage: {Storage}, field: {Field}",
            prefix,
            regex,
            storage,
            fieldName
        );

        // Prepare time patterns
        var timePatterns = _braceExpansionService.PrepareTimePatterns(prefix, regex, fieldName);

        var queryParameters = QueryHelpers.ParseQuery(queryString ?? "");
        var findParameters = queryParameters.Where(parameter =>
            parameter.Key.StartsWith("find[", StringComparison.OrdinalIgnoreCase));
        var findQueryString = QueryString.Create(findParameters);
        string findText = findQueryString.Value?.TrimStart('?') ?? "";

        string mergedFindText = BuildFind(findText, fieldName, timePatterns, type: null);
        var parsedQuery = FindQuery.Parse(mergedFindText);
        var echoQuery = parsedQuery.ToQueryObject();

        var requestQuery = new Dictionary<string, object>();
        foreach (var parameter in queryParameters) {
            if (parameter.Key != "storage" && parameter.Key != "field") {
                requestQuery[parameter.Key] = parameter.Value.ToString();
            }
        }

        return new TimeQueryEcho {
            Req = new TimeQueryRequest {
                Params = new Dictionary<string, string?> {
                    ["prefix"] = prefix,
                    ["regex"] = regex,
                    ["storage"] = storage,
                    ["field"] = fieldName,
                },
                Query = requestQuery,
            },
            Pattern = timePatterns.Patterns,
            Query = echoQuery,
        };
    }

    /// <summary>
    /// Merges the time pattern and slice type with the caller's <c>find[...]</c> keys into one
    /// querystring-form find, the shape every read service parses. A single prefix is ANDed with
    /// the expanded patterns, as upstream does. Expanded patterns become one alternation because
    /// <c>$in</c> compares literally. Repeated <c>$regex</c> keys on one field stay separate
    /// conditions in <see cref="Nocturne.Core.Models.Queries.FindQuery"/>.
    /// </summary>
    /// <exception cref="TimePatternRejectedException">
    /// A composed pattern would not compile as a <c>$regex</c>, so it would silently match nothing.
    /// </exception>
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

        if (patterns.Any(p => !BoundedRegex.Create(p).IsCompiled))
            throw new TimePatternRejectedException(
                "The time pattern is not a supported regular expression: it may not use backreferences "
                    + "or lookarounds, or expand to too large a pattern");

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
