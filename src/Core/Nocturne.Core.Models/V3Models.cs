using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// The order a V3 search asks for: <c>sort=field</c> ascending or <c>sort$desc=field</c> descending.
/// </summary>
/// <param name="Field">The field named by the request, or <c>null</c> when it named none.</param>
/// <param name="Descending">Whether the order is descending. Nightscout's default is ascending.</param>
public sealed record V3Sort(string? Field, bool Descending)
{
    /// <summary>No field named, ascending: Nightscout's order when a request gives neither parameter.</summary>
    public static V3Sort Default { get; } = new(null, false);
}

/// <summary>
/// V3 API query parameters for filtering and field selection.
/// Supports the Nightscout v3 query syntax including field projection, pagination, and filter criteria.
/// </summary>
/// <seealso cref="V3FilterCriteria"/>
/// <seealso cref="V3CollectionResponse{T}"/>
public class V3QueryParameters
{
    /// <summary>
    /// Fields to include in the response (comma-separated)
    /// </summary>
    public string[]? Fields { get; set; }

    public int Limit { get; set; } = 100;

    /// <summary>
    /// Number of records to skip (offset)
    /// </summary>
    public int Offset { get; set; }

    /// <summary>
    /// Sort field and direction
    /// </summary>
    public V3Sort Sort { get; set; } = V3Sort.Default;

    /// <summary>
    /// Filter criteria as JsonElement (legacy)
    /// </summary>
    public JsonElement? Filter { get; set; }

    /// <summary>
    /// Parsed filter criteria in Nightscout V3 format (field$operator=value)
    /// </summary>
    public List<V3FilterCriteria> FilterCriteria { get; set; } = new();

    /// <summary>
    /// If-Modified-Since header value
    /// </summary>
    public DateTimeOffset? IfModifiedSince { get; set; }

    /// <summary>
    /// If-None-Match header value (ETag)
    /// </summary>
    public string? IfNoneMatch { get; set; }
}

/// <summary>
/// V3 API filter criteria (field$operator=value format)
/// </summary>
public class V3FilterCriteria
{
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Filter operator (eq, ne, gt, gte, lt, lte, in, nin, re)
    /// </summary>
    public string Operator { get; set; } = "eq";

    /// <summary>
    /// Value to compare against
    /// </summary>
    public object? Value { get; set; }
}

/// <summary>
/// V3 API collection response wrapper for paginated data.
/// </summary>
/// <seealso cref="V3ResponseMetadata"/>
/// <seealso cref="V3ErrorResponse"/>
public class V3CollectionResponse<T>
{
    public string Status { get; set; } = "ok";

    public List<T> Data { get; set; } = new();

    /// <summary>
    /// Total count of items (before pagination)
    /// </summary>
    public int? Total { get; set; }

    public int? Skip { get; set; }

    public int? Limit { get; set; }

    public V3ResponseMetadata Meta { get; set; } = new();
}

/// <summary>
/// What a V3 create answers: 201 for a stored document, 200 with <see cref="IsDeduplication"/> when
/// the document matched one already stored.
/// </summary>
public class V3CreateResponse
{
    /// <summary>HTTP status of the answer, repeated in the body.</summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }

    /// <summary>Wire identifier of the stored document, which a v3 read by id resolves.</summary>
    [JsonPropertyName("identifier")]
    public string? Identifier { get; set; }

    /// <summary>Set on a create that matched a stored document, which it left unchanged.</summary>
    [JsonPropertyName("isDeduplication")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsDeduplication { get; set; }

    /// <summary>Modification time of the stored document, in Unix milliseconds.</summary>
    [JsonPropertyName("lastModified")]
    public long LastModified { get; set; }
}

/// <summary>
/// V3 API error response
/// </summary>
public class V3ErrorResponse
{
    /// <summary>
    /// Status of the response (typically "error")
    /// </summary>
    public string Status { get; set; } = "error";

    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Error code (as string for V3 compatibility)
    /// </summary>
    public string? Code { get; set; }

    public DateTimeOffset? Timestamp { get; set; }

    public string? Path { get; set; }

    public Dictionary<string, object>? Details { get; set; }
}

/// <summary>
/// V3 API response metadata
/// </summary>
public class V3ResponseMetadata
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public string Version { get; set; } = "3.0";

    /// <summary>
    /// Total execution time in milliseconds
    /// </summary>
    public long? ExecutionTime { get; set; }

    public string? RequestId { get; set; }

    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    /// <summary>
    /// Total count of items (before pagination)
    /// </summary>
    public int TotalCount { get; set; }

    public int Limit { get; set; }

    public int Offset { get; set; }
}
