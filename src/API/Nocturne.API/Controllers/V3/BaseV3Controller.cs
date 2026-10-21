using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.Filters;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// Base controller for V3 API endpoints providing common V3 functionality.
/// Implements pagination, field selection, sorting, filtering, and ETag support
/// as defined by the Nightscout API v3 specification.
/// </summary>
/// <typeparam name="T">The domain model type this controller operates on.</typeparam>
/// <seealso cref="IDocumentProcessingService"/>
/// <seealso cref="V3QueryParameters"/>
/// <seealso cref="V3CollectionResponse{T}"/>
/// <seealso cref="V3FilterCriteria"/>
[ApiController]
[V3ParameterExceptionFilter]
public abstract class BaseV3Controller<T> : ControllerBase
    where T : class
{
    protected readonly IDocumentProcessingService _documentProcessingService;
    protected readonly ILogger _logger;

    private static readonly JsonSerializerOptions DocumentOptions = NightscoutJsonOptions.Create();

    /// <summary>
    /// Initializes the base V3 controller with shared services.
    /// </summary>
    /// <param name="documentProcessingService">Service for processing incoming documents (entries, treatments, etc.).</param>
    /// <param name="logger">Logger instance for the derived controller.</param>
    protected BaseV3Controller(
        IDocumentProcessingService documentProcessingService,
        ILogger logger
    )
    {
        _documentProcessingService = documentProcessingService;
        _logger = logger;
    }

    /// <summary>
    /// Parse V3 query parameters from the request, including limit, offset, fields, sort, filter, and conditional headers.
    /// </summary>
    /// <returns>Parsed <see cref="V3QueryParameters"/> populated from the current request's query string.</returns>
    /// <exception cref="V3ParameterOutOfToleranceException">Thrown when limit is negative, matching Nightscout V3 behavior.</exception>
    /// <exception cref="V3SortConflictException">Thrown when both <c>sort</c> and <c>sort$desc</c> are given and <paramref name="readSort"/> is set.</exception>
    /// <param name="readSort">Whether the request's sort and limit are read; a read of one document by identifier ignores both, as Nightscout does.</param>
    protected V3QueryParameters ParseV3QueryParameters(bool readSort = true)
    {
        var query = HttpContext.Request.Query;

        var rawLimit = ParseIntParameter(query, "limit", 100);

        // Nightscout V3 API returns 400 for negative limit with "Parameter limit out of tolerance"
        if (readSort && rawLimit < 0)
        {
            throw new V3ParameterOutOfToleranceException("limit");
        }

        var parameters = new V3QueryParameters
        {
            Limit = rawLimit,
            Offset = ParseIntParameter(query, "offset", 0),
            Fields = ParseStringArrayParameter(query, "fields"),
            Sort = readSort ? ParseSort(query) : V3Sort.Default,
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
        if (dateFields.Contains(field) && UploaderTimestamp.TryParse(rawValue, out var dateValue))
        {
            return dateValue.ToUnixTimeMilliseconds();
        }

        return rawValue;
    }

    /// <summary>
    /// Every operator on a field lands in one operator object, as Nightscout's storage layer builds
    /// it, so <c>date$gte=A&amp;date$lte=B</c> is a closed range.
    /// </summary>
    internal static string? ConvertFilterCriteriaToFindQuery(List<V3FilterCriteria>? filterCriteria)
    {
        if (filterCriteria == null || filterCriteria.Count == 0)
            return null;

        var operatorsByField = new Dictionary<string, Dictionary<string, object?>>();

        foreach (var criteria in filterCriteria)
        {
            var mongoOp = criteria.Operator switch
            {
                "eq" => "$eq",
                "ne" => "$ne",
                "gt" => "$gt",
                "gte" => "$gte",
                "lt" => "$lt",
                "lte" => "$lte",
                "in" => "$in",
                "nin" => "$nin",
                "re" => "$regex",
                _ => null,
            };
            if (mongoOp == null)
                continue;

            if (!operatorsByField.TryGetValue(criteria.Field, out var operators))
            {
                operators = new Dictionary<string, object?>();
                operatorsByField[criteria.Field] = operators;
            }

            operators[mongoOp] = mongoOp == "$eq" ? criteria.Value ?? "" : criteria.Value;
        }

        if (operatorsByField.Count == 0)
            return null;

        var conditions = operatorsByField.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Count == 1 && pair.Value.TryGetValue("$eq", out var equality)
                ? equality
                : pair.Value
        );

        return JsonSerializer.Serialize(conditions);
    }

    /// <summary>
    /// Serializes <paramref name="document"/> as the Nightscout wire shape and stamps the v3
    /// <c>identifier</c>, also served as <c>_id</c>, as the 24-hex ObjectId a by-id read resolves.
    /// </summary>
    protected static JsonObject ToV3Document<TDocument>(TDocument document, string? id)
    {
        var node = JsonSerializer.SerializeToNode(document, DocumentOptions)!.AsObject();
        if (MongoObjectId.Coerce(id) is { } identifier)
        {
            node["_id"] = identifier;
            node["identifier"] = identifier;
        }

        return node;
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
    /// Format a V3 ETag from a record timestamp. AAPS parses every V3 ETag by stripping the
    /// first three characters (<c>W/"</c>) and the trailing quote, then calling
    /// <c>toLong()</c> — so the value between the quotes MUST be a bare Unix-milliseconds
    /// number. Any other shape (e.g. a content hash) crashes the AAPS sync loop with
    /// <c>NumberFormatException</c> (#522).
    /// </summary>
    /// <param name="maxMills">Highest record timestamp in the response, in Unix milliseconds.</param>
    protected static string FormatCursorETag(long maxMills) => $"W/\"{maxMills}\"";

    /// <summary>
    /// Set V3 response headers including pagination, ETag, and caching
    /// </summary>
    /// <param name="data">Response data</param>
    /// <param name="parameters">Query parameters</param>
    /// <param name="lastModified">
    /// Newest record timestamp when the caller already knows it (e.g. computed from domain
    /// models before mapping to DTOs the timestamp reflection can't read).
    /// </param>
    protected void SetV3ResponseHeaders<TItem>(
        IReadOnlyCollection<TItem> data,
        V3QueryParameters parameters,
        DateTimeOffset? lastModified = null
    )
    {
        // Cursor-style ETag from the newest record; falls back to the current time for
        // collections whose items carry no recognizable timestamp.
        var effectiveLastModified =
            lastModified ?? GetLastModified(data.Cast<object>()) ?? DateTimeOffset.UtcNow;
        Response.Headers["ETag"] = FormatCursorETag(
            effectiveLastModified.ToUnixTimeMilliseconds()
        );

        Response.Headers["X-Limit"] = parameters.Limit.ToString();
        Response.Headers["X-Offset"] = parameters.Offset.ToString();

        // Set Link header for pagination
        var baseUrl = $"{Request.PublicScheme()}://{Request.Host}{Request.Path}";
        var links = new List<string>();

        if (parameters.Offset > 0)
        {
            var prevOffset = Math.Max(0, parameters.Offset - parameters.Limit);
            links.Add($"<{baseUrl}?limit={parameters.Limit}&offset={prevOffset}>; rel=\"prev\"");
        }

        // A search does not count the collection; a full page is the only sign of another.
        if (data.Count >= parameters.Limit)
        {
            var nextOffset = parameters.Offset + parameters.Limit;
            links.Add($"<{baseUrl}?limit={parameters.Limit}&offset={nextOffset}>; rel=\"next\"");
        }

        if (links.Count > 0)
        {
            Response.Headers["Link"] = string.Join(", ", links);
        }

        // Tenant reads must not be shared-cacheable: UseResponseCaching answers before
        // authentication runs, so a stored entry would outlive the credential check.
        Response.Headers["Cache-Control"] = "private, max-age=60";
        Response.Headers["Last-Modified"] = effectiveLastModified.UtcDateTime.ToString("R");
        Response.Headers["Vary"] = "Accept, If-Modified-Since, If-None-Match";
    }

    /// <summary>
    /// Check if the request can return a 304 Not Modified response.
    /// The current ETag is derived from <paramref name="lastModified"/> with
    /// <see cref="FormatCursorETag"/>, matching what the response headers carry.
    /// </summary>
    /// <param name="lastModified">Last modified timestamp of the response data</param>
    /// <param name="parameters">Query parameters</param>
    /// <returns>True if 304 should be returned</returns>
    protected bool ShouldReturn304(DateTimeOffset lastModified, V3QueryParameters parameters)
    {
        // Check If-None-Match header (tolerate weak markers and quoting variations)
        if (!string.IsNullOrEmpty(parameters.IfNoneMatch))
        {
            var clientETag = NormalizeETag(parameters.IfNoneMatch);
            var currentETag = NormalizeETag(
                FormatCursorETag(lastModified.ToUnixTimeMilliseconds())
            );
            if (clientETag == currentETag)
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
    /// Parse a legacy <c>created_at</c> string into Unix milliseconds.
    /// </summary>
    protected static long? ParseCreatedAtMills(string? createdAt)
    {
        if (string.IsNullOrEmpty(createdAt))
            return null;

        return DateTimeOffset.TryParse(
            createdAt,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed
        )
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }

    private static string NormalizeETag(string etag)
    {
        var value = etag.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }
        return value.Trim('"');
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
        return Ok(new V3Envelope<object> { Result = result });
    }

    /// <summary>
    /// Create the V3 success response for a delete: <c>{"status": 200}</c> with no result.
    /// AAPS treats any other success status on DELETE as unknown and stops syncing the collection.
    /// </summary>
    protected ActionResult CreateV3DeleteResponse()
    {
        return Ok(new { status = 200 });
    }

    /// <summary>
    /// The V3 answer to a create that stored a new document: 201 with the document's wire
    /// identifier and the server's write time as <c>lastModified</c>, <c>Location</c> pointing at
    /// <paramref name="getAction"/>, and <c>Last-Modified</c>.
    /// </summary>
    /// <param name="getAction">Name of the action that reads one document of the collection.</param>
    /// <param name="storedId">Stored id of the created document.</param>
    protected ActionResult V3Created(string getAction, string? storedId) =>
        V3Inserted(getAction, MongoObjectId.Coerce(storedId));

    /// <summary>
    /// The V3 answer to a write that stored a new document under <paramref name="identifier"/>,
    /// already in its wire form: 201 with the identifier and the server's write time as
    /// <c>lastModified</c>, <c>Location</c> pointing at <paramref name="getAction"/>, and
    /// <c>Last-Modified</c>.
    /// </summary>
    protected ActionResult V3Inserted(string getAction, string? identifier)
    {
        var lastModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Response.Headers["Last-Modified"] = FormatHttpDate(lastModified);
        return CreatedAtAction(
            getAction,
            new { id = identifier },
            new V3CreateResponse { Status = 201, Identifier = identifier, LastModified = lastModified }
        );
    }

    /// <summary>
    /// The V3 answer to a PUT that replaced a stored document: 200 with the server's write time as
    /// <c>lastModified</c>, and <c>Last-Modified</c>.
    /// </summary>
    protected ActionResult V3Replaced()
    {
        var lastModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Response.Headers["Last-Modified"] = FormatHttpDate(lastModified);
        return Ok(new { status = 200, lastModified });
    }

    /// <summary>
    /// Validates common V3 fields as per Nightscout API behavior.
    /// Nightscout validates these fields before checking if the document exists.
    /// </summary>
    /// <param name="jsonData">The JSON data to validate</param>
    /// <returns>ActionResult with error if validation fails, null if validation passes</returns>
    protected ActionResult? ValidateV3CommonFields(JsonElement jsonData)
    {
        const long MinTimestamp = 946684800000; // Year 2000 in milliseconds

        // A document is an object; any other body has no fields to read, and upstream refuses it.
        if (jsonData.ValueKind != JsonValueKind.Object)
        {
            return CreateV3ErrorResponse(400, "Bad or missing request body");
        }

        // Check for 'date' field - Nightscout requires this for all V3 operations
        if (!jsonData.TryGetProperty("date", out var dateProperty))
        {
            return CreateV3ErrorResponse(400, "Bad or missing date field");
        }

        // Date must be a number greater than MIN_TIMESTAMP
        if (dateProperty.ValueKind != JsonValueKind.Number)
        {
            return CreateV3ErrorResponse(400, "Bad or missing date field");
        }

        if (!dateProperty.TryGetInt64(out var dateValue) || dateValue <= MinTimestamp)
        {
            return CreateV3ErrorResponse(400, "Bad or missing date field");
        }

        return null; // Validation passed
    }

    /// <summary>
    /// The V3 answer to a create that matched a document already stored: 200 with
    /// <c>isDeduplication</c>, the stored document's identifier, which a v3 read by id resolves, and
    /// its <c>lastModified</c>. The stored document is left as it was.
    /// </summary>
    /// <param name="storedId">Stored id of the existing document.</param>
    /// <param name="srvModified">The existing document's <c>srvModified</c>, in Unix milliseconds.</param>
    protected ActionResult V3Deduplicated(string? storedId, long? srvModified)
    {
        var lastModified = srvModified ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Response.Headers["Last-Modified"] = FormatHttpDate(lastModified);
        return Ok(
            new V3CreateResponse
            {
                Status = 200,
                Identifier = MongoObjectId.Coerce(storedId),
                IsDeduplication = true,
                LastModified = lastModified,
            }
        );
    }

    private static string FormatHttpDate(long mills) =>
        DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime.ToString("R");

    /// <summary>
    /// Set <c>Last-Modified</c> and <c>ETag</c> on a V3 <c>history/{lastModified}</c> response.
    /// AAPS advances its incremental-sync cursor from these headers; without them the cursor
    /// stays put and the same page is requested repeatedly.
    /// </summary>
    /// <param name="maxMills">Highest record timestamp in the response, in Unix milliseconds.</param>
    protected void SetHistoryCursorHeaders(long maxMills)
    {
        Response.Headers["Last-Modified"] = FormatHttpDate(maxMills);
        Response.Headers["ETag"] = FormatCursorETag(maxMills);
    }

    /// <summary>
    /// Create a standardized V3 success response with metadata including pagination headers.
    /// Returns the Nightscout V3-compatible envelope: <c>{"status": 200, "result": [...]}</c>.
    /// </summary>
    /// <typeparam name="TItem">The type of items in the collection.</typeparam>
    /// <returns>The Nightscout V3 envelope around <paramref name="data"/>.</returns>
    protected IActionResult CreateV3CollectionResponse<TItem>(
        IReadOnlyCollection<TItem> data,
        V3QueryParameters parameters,
        DateTimeOffset? lastModified = null
    )
    {
        var responseData =
            parameters.Fields != null
                ? ApplyFieldSelection(data, parameters.Fields)
                : data.Cast<object>();

        SetV3ResponseHeaders(data, parameters, lastModified);

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

    private V3Sort ParseSort(IQueryCollection query)
    {
        var ascending = ParseStringParameter(query, "sort");
        var descending = ParseStringParameter(query, "sort$desc");

        return (ascending, descending) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => throw new V3SortConflictException(),
            (_, { Length: > 0 }) => new V3Sort(descending, true),
            ({ Length: > 0 }, _) => new V3Sort(ascending, false),
            _ => V3Sort.Default,
        };
    }

    /// <summary>The read-side sort field a request names; any field a store cannot order by is time.</summary>
    protected static ReadSortField ReadSortFieldOf(V3Sort sort) => sort.Field switch
    {
        "srvModified" => ReadSortField.SrvModified,
        "sgv" => ReadSortField.Sgv,
        _ => ReadSortField.Time,
    };

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
                if (UploaderTimestamp.TryParse(value, out var result))
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
    /// The V3 JSON filter as a find query. The find parser reads the JSON form directly, so the
    /// operator and value structure passes through untouched.
    /// </summary>
    /// <param name="filter">V3 filter object</param>
    /// <returns>The filter's JSON text, or null when there is no filter</returns>
    protected static string? ConvertV3FilterToV1Find(JsonElement? filter) => filter?.GetRawText();

    /// <summary>
    /// Extract type filter from V3 filter
    /// </summary>
    /// <param name="filter">V3 filter object</param>
    /// <returns>Type value or null</returns>
    protected string? ExtractTypeFromFilter(JsonElement? filter)
    {
        if (
            filter is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty("type", out var typeProperty)
            && typeProperty.ValueKind == JsonValueKind.String
        )
        {
            return typeProperty.GetString();
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

                // Check for SrvModified property (DateTimeOffset or Unix milliseconds)
                var srvModified = itemType.GetProperty("SrvModified")?.GetValue(item) switch
                {
                    DateTimeOffset dto => dto,
                    long srvMills => DateTimeOffset.FromUnixTimeMilliseconds(srvMills),
                    _ => (DateTimeOffset?)null,
                };
                if (srvModified != null)
                {
                    itemModified = srvModified;
                }

                // Check for CreatedAt property (DateTimeOffset or ISO-8601 string)
                var createdAtProperty = itemType.GetProperty("CreatedAt");
                var createdAt = createdAtProperty?.GetValue(item) switch
                {
                    DateTimeOffset dto => dto,
                    string s when ParseCreatedAtMills(s) is long createdAtMills =>
                        DateTimeOffset.FromUnixTimeMilliseconds(createdAtMills),
                    _ => (DateTimeOffset?)null,
                };
                if (createdAt != null && (itemModified == null || createdAt > itemModified))
                {
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
