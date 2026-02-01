using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// Base controller for V3 API endpoints providing common V3 functionality
/// Implements pagination, field selection, sorting, filtering, and ETag support
/// </summary>
[ApiController]
public abstract class BaseV3Controller<T> : ControllerBase
    where T : class
{
    protected readonly IPostgreSqlService _postgreSqlService;
    protected readonly IDataFormatService _dataFormatService;
    protected readonly IDocumentProcessingService _documentProcessingService;
    protected readonly ILogger _logger;

    protected BaseV3Controller(
        IPostgreSqlService postgreSqlService,
        IDataFormatService dataFormatService,
        IDocumentProcessingService documentProcessingService,
        ILogger logger
    )
    {
        _postgreSqlService = postgreSqlService;
        _dataFormatService = dataFormatService;
        _documentProcessingService = documentProcessingService;
        _logger = logger;
    }

    /// <summary>
    /// Parse V3 query parameters from the request
    /// </summary>
    /// <returns>Parsed V3 query parameters</returns>
    /// <exception cref="ArgumentException">Thrown when limit is out of tolerance (negative)</exception>
    protected V3QueryParameters ParseV3QueryParameters()
    {
        var query = HttpContext.Request.Query;

        var rawLimit = ParseIntParameter(query, "limit", 100);

        // Nightscout V3 API returns 400 for negative limit with "Parameter limit out of tolerance"
        if (rawLimit < 0)
        {
            throw new V3ParameterOutOfToleranceException("limit");
        }

        var parameters = new V3QueryParameters
        {
            Limit = rawLimit,
            Offset = ParseIntParameter(query, "offset", 0),
            Fields = ParseStringArrayParameter(query, "fields"),
            Sort = ParseStringParameter(query, "sort"),
            Filter = ParseJsonParameter(query, "filter"),
            IfModifiedSince = ParseDateTimeParameter(query, "if-modified-since"),
            IfNoneMatch = ParseStringParameter(query, "if-none-match"),
            FilterCriteria = ParseV3FilterCriteria(query),
        };

        // Handle skip as alias for offset
        var skip = ParseIntParameter(query, "skip", -1);
        if (skip >= 0)
        {
            parameters.Offset = skip;
        }

        // Validate limits to prevent abuse
        const int MaxEntriesLimit = 1_000; // Align with Nightscout API v3 legacy limit
        if (parameters.Limit < 1)
            parameters.Limit = 1;
        if (parameters.Limit > MaxEntriesLimit)
            parameters.Limit = MaxEntriesLimit;
        if (parameters.Offset < 0)
            parameters.Offset = 0;

        return parameters;
    }

    /// <summary>
    /// Exception thrown when a V3 API parameter is out of tolerance
    /// </summary>
    public class V3ParameterOutOfToleranceException : Exception
    {
        public string ParameterName { get; }

        public V3ParameterOutOfToleranceException(string parameterName)
            : base($"Parameter {parameterName} out of tolerance")
        {
            ParameterName = parameterName;
        }
    }

    /// <summary>
    /// Parse Nightscout V3 filter criteria from query parameters
    /// Format: field$operator=value (e.g., category$eq=Fruit, carbs$gte=15)
    /// </summary>
    /// <param name="query">Query collection</param>
    /// <returns>List of parsed filter criteria</returns>
    private List<V3FilterCriteria> ParseV3FilterCriteria(IQueryCollection query)
    {
        var filterCriteria = new List<V3FilterCriteria>();
        var reservedParams = new HashSet<string>
        {
            "token",
            "sort",
            "sort$desc",
            "limit",
            "skip",
            "offset",
            "fields",
            "now",
            "filter",
            "if-modified-since",
            "if-none-match",
        };
        var validOperators = new HashSet<string>
        {
            "eq",
            "ne",
            "gt",
            "gte",
            "lt",
            "lte",
            "in",
            "nin",
            "re",
        };
        var filterRegex = new System.Text.RegularExpressions.Regex(@"^(.+)\$([a-zA-Z]+)$");

        foreach (var param in query)
        {
            if (reservedParams.Contains(param.Key.ToLower()))
                continue;

            var field = param.Key;
            var op = "eq";

            var match = filterRegex.Match(param.Key);
            if (match.Success)
            {
                field = match.Groups[1].Value;
                op = match.Groups[2].Value.ToLower();

                if (!validOperators.Contains(op))
                {
                    _logger.LogWarning("Unsupported filter operator: {Operator}", op);
                    continue;
                }
            }

            var rawValue = param.Value.FirstOrDefault();
            var value = ParseFilterValue(field, rawValue);

            filterCriteria.Add(
                new V3FilterCriteria
                {
                    Field = field,
                    Operator = op,
                    Value = value,
                }
            );
        }

        return filterCriteria;
    }

    /// <summary>
    /// Parse and convert filter value to appropriate type
    /// </summary>
    private object? ParseFilterValue(string field, string? rawValue)
    {
        if (string.IsNullOrEmpty(rawValue))
            return null;

        // Try to parse as number
        if (double.TryParse(rawValue, out var numValue))
            return numValue;

        // Parse boolean strings
        if (rawValue.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (rawValue.Equals("false", StringComparison.OrdinalIgnoreCase))
            return false;

        // Unwrap string in single quotes
        if (rawValue.StartsWith('\'') && rawValue.EndsWith('\'') && rawValue.Length >= 2)
            return rawValue[1..^1];

        // Parse date fields
        var dateFields = new HashSet<string> { "date", "srvModified", "srvCreated", "created_at" };
        if (dateFields.Contains(field) && DateTimeOffset.TryParse(rawValue, out var dateValue))
        {
            return dateValue.ToUnixTimeMilliseconds();
        }

        return rawValue;
    }

    /// <summary>
    /// Apply field selection to a collection of objects
    /// </summary>
    /// <param name="data">Data to filter</param>
    /// <param name="fields">Fields to include</param>
    /// <returns>Filtered data with only selected fields</returns>
    protected IEnumerable<object> ApplyFieldSelection<TItem>(
        IEnumerable<TItem> data,
        string[]? fields
    )
    {
        if (fields == null || fields.Length == 0)
        {
            return data.Cast<object>();
        }

        return data.Select(item =>
        {
            var json = JsonSerializer.Serialize(item);
            var document = JsonDocument.Parse(json);
            var filteredObject = new Dictionary<string, object?>();

            foreach (var field in fields)
            {
                if (document.RootElement.TryGetProperty(field, out var property))
                {
                    filteredObject[field] = JsonSerializer.Deserialize<object>(property);
                }
            }

            return filteredObject;
        });
    }

    /// <summary>
    /// Generate ETag for a collection of data
    /// </summary>
    /// <param name="data">Data to generate ETag for</param>
    /// <returns>ETag value</returns>
    protected string GenerateETag<TItem>(IEnumerable<TItem> data)
    {
        var json = JsonSerializer.Serialize(data);
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        return Convert.ToHexString(hash)[..16]; // Use first 16 characters
    }

    /// <summary>
    /// Set V3 response headers including pagination, ETag, and caching
    /// </summary>
    /// <param name="data">Response data</param>
    /// <param name="parameters">Query parameters</param>
    /// <param name="totalCount">Total count of items (for pagination)</param>
    protected void SetV3ResponseHeaders<TItem>(
        IEnumerable<TItem> data,
        V3QueryParameters parameters,
        long totalCount
    )
    {
        // Set ETag header
        var etag = GenerateETag(data);
        Response.Headers["ETag"] = $"\"{etag}\"";

        // Set pagination headers
        Response.Headers["X-Total-Count"] = totalCount.ToString();
        Response.Headers["X-Limit"] = parameters.Limit.ToString();
        Response.Headers["X-Offset"] = parameters.Offset.ToString();

        // Set Link header for pagination
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.Path}";
        var links = new List<string>();

        if (parameters.Offset > 0)
        {
            var prevOffset = Math.Max(0, parameters.Offset - parameters.Limit);
            links.Add($"<{baseUrl}?limit={parameters.Limit}&offset={prevOffset}>; rel=\"prev\"");
        }

        if (parameters.Offset + parameters.Limit < totalCount)
        {
            var nextOffset = parameters.Offset + parameters.Limit;
            links.Add($"<{baseUrl}?limit={parameters.Limit}&offset={nextOffset}>; rel=\"next\"");
        }

        if (links.Count > 0)
        {
            Response.Headers["Link"] = string.Join(", ", links);
        }

        // Set cache control headers
        Response.Headers["Cache-Control"] = "public, max-age=60";
        Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");
        Response.Headers["Vary"] = "Accept, If-Modified-Since, If-None-Match";
    }

    /// <summary>
    /// Check if the request can return a 304 Not Modified response
    /// </summary>
    /// <param name="etag">Current ETag</param>
    /// <param name="lastModified">Last modified timestamp</param>
    /// <param name="parameters">Query parameters</param>
    /// <returns>True if 304 should be returned</returns>
    protected bool ShouldReturn304(
        string etag,
        DateTimeOffset lastModified,
        V3QueryParameters parameters
    )
    {
        // Check If-None-Match header
        if (!string.IsNullOrEmpty(parameters.IfNoneMatch))
        {
            var clientETag = parameters.IfNoneMatch.Trim('"');
            if (clientETag == etag)
            {
                return true;
            }
        }

        // Check If-Modified-Since header
        if (parameters.IfModifiedSince.HasValue)
        {
            if (lastModified <= parameters.IfModifiedSince.Value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Create a standardized V3 error response (Nightscout compatible)
    /// Nightscout V3 API returns simple {"status": STATUS_CODE} for errors
    /// </summary>
    /// <param name="statusCode">HTTP status code</param>
    /// <param name="message">Error message (unused in Nightscout format, kept for logging)</param>
    /// <param name="details">Additional error details (unused in Nightscout format)</param>
    /// <returns>Nightscout-compatible error response</returns>
    protected ActionResult CreateV3ErrorResponse(
        int statusCode,
        string message,
        object? details = null
    )
    {
        _logger.LogDebug("V3 error response: {StatusCode} - {Message}", statusCode, message);

        // Nightscout V3 API returns {"status": STATUS_CODE, "message": "..."} for errors
        return StatusCode(statusCode, new { status = statusCode, message });
    }

    /// <summary>
    /// Create a Nightscout V3-compatible success response
    /// Nightscout V3 API returns {"status": 200, "result": DATA} for success
    /// </summary>
    /// <param name="result">Result data</param>
    /// <returns>Nightscout-compatible success response</returns>
    protected ActionResult CreateV3SuccessResponse(object result)
    {
        return Ok(new { status = 200, result });
    }

    /// <summary>
    /// Create a standardized V3 success response with metadata
    /// </summary>
    /// <param name="data">Response data</param>
    /// <param name="parameters">Query parameters</param>
    /// <param name="totalCount">Total count for pagination</param>
    /// <returns>Standardized success response</returns>
    protected IActionResult CreateV3CollectionResponse<TItem>(
        IEnumerable<TItem> data,
        V3QueryParameters parameters,
        long totalCount
    )
    {
        // Apply field selection
        var responseData =
            parameters.Fields != null
                ? ApplyFieldSelection(data, parameters.Fields)
                : data.Cast<object>();

        var response = new V3CollectionResponse<object>
        {
            Data = responseData.ToList(),
            Meta = new V3ResponseMetadata
            {
                TotalCount = (int)totalCount,
                Limit = parameters.Limit,
                Offset = parameters.Offset,
                Timestamp = DateTimeOffset.UtcNow,
                Version = "3.0",
            },
        };

        // Set response headers
        SetV3ResponseHeaders(data, parameters, totalCount);

        // Nightscout V3 API returns {"status": 200, "result": [...]}
        return Ok(
            new Dictionary<string, object> { ["status"] = 200, ["result"] = responseData.ToList() }
        );
    }

    #region Parameter Parsing Helpers

    private int ParseIntParameter(IQueryCollection query, string key, int defaultValue)
    {
        if (
            query.TryGetValue(key, out var values)
            && int.TryParse(values.FirstOrDefault(), out var result)
        )
        {
            return result;
        }
        return defaultValue;
    }

    private string? ParseStringParameter(IQueryCollection query, string key)
    {
        if (query.TryGetValue(key, out var values))
        {
            return values.FirstOrDefault();
        }
        return null;
    }

    private string[]? ParseStringArrayParameter(IQueryCollection query, string key)
    {
        if (query.TryGetValue(key, out var values))
        {
            var value = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
            {
                return value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToArray();
            }
        }
        return null;
    }

    private JsonElement? ParseJsonParameter(IQueryCollection query, string key)
    {
        if (query.TryGetValue(key, out var values))
        {
            var value = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
            {
                try
                {
                    return JsonSerializer.Deserialize<JsonElement>(value);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to parse JSON parameter {Key}: {Value}",
                        key,
                        value
                    );
                }
            }
        }
        return null;
    }

    private DateTimeOffset? ParseDateTimeParameter(IQueryCollection query, string key)
    {
        if (query.TryGetValue(key, out var values))
        {
            var value = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
            {
                if (DateTimeOffset.TryParse(value, out var result))
                {
                    return result;
                }
            }
        }
        return null;
    }

    #endregion

    #region Protected Helper Methods

    /// <summary>
    /// Convert V3 filter to V1 find query string
    /// </summary>
    /// <param name="filter">V3 filter object</param>
    /// <returns>V1 find query string</returns>
    protected string? ConvertV3FilterToV1Find(JsonElement? filter)
    {
        if (filter == null || !filter.HasValue)
            return null;

        try
        {
            // Convert JSON filter to MongoDB query string
            return filter.Value.GetRawText();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert V3 filter to V1 find query");
            return null;
        }
    }

    /// <summary>
    /// Extract sort direction from V3 sort parameter
    /// </summary>
    /// <param name="sort">V3 sort parameter</param>
    /// <returns>True for reverse sorting (ascending), false for default (descending)</returns>
    protected bool ExtractSortDirection(string? sort)
    {
        if (string.IsNullOrEmpty(sort))
            return true; // Nightscout defaults to Ascending (Oldest first)

        // Check if sort starts with '-' (descending) or 'desc' (descending)
        if (sort.StartsWith('-') || sort.StartsWith("desc", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Default to ascending (true) if just field name is provided
        return true;
    }

    /// <summary>
    /// Extract type filter from V3 filter
    /// </summary>
    /// <param name="filter">V3 filter object</param>
    /// <returns>Type value or null</returns>
    protected string? ExtractTypeFromFilter(JsonElement? filter)
    {
        if (filter == null || !filter.HasValue)
            return null;

        try
        {
            if (
                filter.Value.ValueKind == JsonValueKind.Object
                && filter.Value.TryGetProperty("type", out var typeProperty)
            )
            {
                return typeProperty.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract type from V3 filter");
        }

        return null;
    }

    /// <summary>
    /// Get last modified timestamp from collection
    /// </summary>
    /// <param name="items">Collection of items</param>
    /// <returns>Last modified timestamp or null</returns>
    protected DateTimeOffset? GetLastModified(IEnumerable<object> items)
    {
        if (!items.Any())
            return null;

        try
        {
            DateTimeOffset? lastModified = null;

            foreach (var item in items)
            {
                DateTimeOffset? itemModified = null;

                // Try to get timestamp from various possible properties
                var itemType = item.GetType();

                // Check for Mills property
                var millsProperty = itemType.GetProperty("Mills");
                if (millsProperty != null && millsProperty.GetValue(item) is long mills)
                {
                    itemModified = DateTimeOffset.FromUnixTimeMilliseconds(mills);
                }

                // Check for SrvModified property
                var srvModifiedProperty = itemType.GetProperty("SrvModified");
                if (
                    srvModifiedProperty != null
                    && srvModifiedProperty.GetValue(item) is DateTimeOffset srvModified
                )
                {
                    itemModified = srvModified;
                }

                // Check for CreatedAt property
                var createdAtProperty = itemType.GetProperty("CreatedAt");
                if (
                    createdAtProperty != null
                    && createdAtProperty.GetValue(item) is DateTimeOffset createdAt
                )
                {
                    if (itemModified == null || createdAt > itemModified)
                        itemModified = createdAt;
                }

                if (itemModified != null && (lastModified == null || itemModified > lastModified))
                {
                    lastModified = itemModified;
                }
            }

            return lastModified;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get last modified timestamp");
            return null;
        }
    }

    /// <summary>
    /// Parse create request from request body
    /// </summary>
    /// <typeparam name="TRequest">Type of the request object</typeparam>
    /// <returns>Parsed request object or null</returns>
    protected async Task<TRequest?> ParseCreateRequest<TRequest>()
        where TRequest : class
    {
        try
        {
            if (Request.ContentLength == 0)
                return null;

            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();

            if (string.IsNullOrEmpty(body))
                return null;

            return JsonSerializer.Deserialize<TRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse create request");
            return null;
        }
    }

    /// <summary>
    /// Create V3 collection response
    /// </summary>
    /// <typeparam name="TData">Type of the data</typeparam>
    /// <param name="data">Response data</param>
    /// <param name="totalCount">Total count for pagination</param>
    /// <param name="lastModified">Last modified timestamp</param>
    /// <returns>V3 collection response</returns>
    protected ActionResult<V3CollectionResponse<object>> CreateV3Response<TData>(
        IEnumerable<TData> data,
        long? totalCount = null,
        DateTimeOffset? lastModified = null
    )
    {
        var response = new V3CollectionResponse<object>
        {
            Data = data.Cast<object>().ToList(),
            Meta = new V3ResponseMetadata
            {
                TotalCount = (int)(totalCount ?? 0),
                Timestamp = lastModified ?? DateTimeOffset.UtcNow,
            },
        };

        return Ok(response);
    }

    #endregion
}
