using System.Text.RegularExpressions;
using Nocturne.API.Extensions;
using Nocturne.API.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Time query controller that provides 1:1 compatibility with Nightscout time-based endpoints.
/// Supports bash-style brace expansion for complex time pattern matching.
/// </summary>
/// <seealso cref="ITimeQueryService"/>
/// <remarks>
/// The <c>slice</c> and <c>echo</c> actions take the collection as a route or query value, so
/// their governing scope is resolved per request through <see cref="LegacyStorageReadScopes"/>; a
/// class-level OR would let a caller holding one category read another's records. The <c>times</c>
/// actions always read entries, so they carry <c>glucose.read</c> directly.
/// </remarks>
[ApiController]
[Tags("V1")]
[Route("api/v1")]
public class TimeQueryController : ControllerBase
{
    /// <summary>
    /// The collections <c>slice</c> and <c>times/echo</c> dispatch on. Every one must be classified
    /// in <see cref="LegacyStorageReadScopes"/>, which <c>SliceableStorage_IsFullyClassified</c>
    /// asserts.
    /// </summary>
    internal static readonly string[] SliceableStorage = ["entries", "treatments", "devicestatus"];

    private static readonly Regex SafeFieldName = new(
        @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private readonly ITimeQueryService _timeQueryService;
    private readonly ILogger<TimeQueryController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TimeQueryController"/>.
    /// </summary>
    /// <param name="timeQueryService">Service for time-pattern-based entry queries.</param>
    /// <param name="logger">Logger instance.</param>
    public TimeQueryController(
        ITimeQueryService timeQueryService,
        ILogger<TimeQueryController> logger
    )
    {
        _timeQueryService = timeQueryService;
        _logger = logger;
    }

    /// <summary>
    /// /api/v1/times without prefix is not a valid Nightscout endpoint.
    /// Nightscout returns 404 for this path. We match that behavior for parity, behind the same
    /// scope the prefixed routes carry so the 404 is not itself readable without a grant.
    /// </summary>
    /// <returns>404 Not Found to match Nightscout behavior</returns>
    [HttpGet("times")]
    [ApiExplorerSettings(IgnoreApi = true)] // Hide from OpenAPI as it's not a real endpoint
    [RequireScope(Scope.GlucoseRead)]
    public ActionResult GetTimeBasedEntries()
    {
        return NotFound();
    }

    /// <summary>
    /// Complex time pattern matching with bash-style brace expansion
    /// </summary>
    /// <param name="prefix">Time prefix pattern (e.g., "2015-04", "20{14..15}")</param>
    /// <param name="count">Maximum number of entries to return (default: 10)</param>
    /// <param name="format">Output format (json, csv, tsv, txt)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Entries matching the time patterns</returns>
    [HttpGet("times/{prefix}")]
    [NightscoutEndpoint("/api/v1/times/{prefix}")]
    [RequireScope(Scope.GlucoseRead)]
    [ProducesResponseType(typeof(Entry[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetTimeBasedEntriesWithPrefix(
        string prefix,
        [FromQuery] int count = 10,
        [FromQuery] string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        return await GetTimeBasedEntriesInternal(prefix, null, count, format, cancellationToken);
    }

    /// <summary>
    /// Complex time pattern matching with bash-style brace expansion
    /// </summary>
    /// <param name="prefix">Time prefix pattern (e.g., "2015-04", "20{14..15}")</param>
    /// <param name="regex">Time regex pattern (e.g., "T{13..18}:{00..15}")</param>
    /// <param name="count">Maximum number of entries to return (default: 10)</param>
    /// <param name="format">Output format (json, csv, tsv, txt)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Entries matching the time patterns</returns>
    [HttpGet("times/{prefix}/{regex}")]
    [NightscoutEndpoint("/api/v1/times/{prefix}/{regex}")]
    [RequireScope(Scope.GlucoseRead)]
    [ProducesResponseType(typeof(Entry[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetTimeBasedEntriesWithPrefixAndRegex(
        string prefix,
        string regex,
        [FromQuery] int count = 10,
        [FromQuery] string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        return await GetTimeBasedEntriesInternal(prefix, regex, count, format, cancellationToken);
    }

    private async Task<ActionResult> GetTimeBasedEntriesInternal(
        string? prefix = null,
        string? regex = null,
        int count = 10,
        string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Time-based query requested - prefix: {Prefix}, regex: {Regex}, count: {Count} from {RemoteIpAddress}",
            prefix,
            regex,
            count,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Validate parameters
            if (count < 0)
            {
                _logger.LogWarning("Invalid count parameter: {Count}", count);
                return BadRequest($"Count must be non-negative, got {count}");
            }

            // Execute time-based query
            var entries = await _timeQueryService.ExecuteTimeQueryAsync(
                prefix: prefix,
                regex: regex,
                storage: "entries",
                fieldName: "dateString",
                find: FindParameters(),
                cancellationToken: cancellationToken
            );

            var entriesArray = entries.Take(count).ToArray();

            _logger.LogDebug(
                "Successfully returned {Count} entries for time query",
                entriesArray.Length
            );

            return EntriesResult(entriesArray, format);
        }
        catch (TimePatternRejectedException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error executing time-based query with prefix: {Prefix}, regex: {Regex}",
                prefix,
                regex
            );
            return StatusCode(
                500,
                new
                {
                    status = 500,
                    message = "Internal server error",
                    type = "internal",
                    error = ex.Message,
                }
            );
        }
    }

    /// <summary>
    /// Time query debugging and pattern inspection
    /// Shows how the time patterns are expanded and what MongoDB query would be generated
    /// </summary>
    /// <param name="storage">Storage type (entries, treatments, devicestatus)</param>
    /// <param name="field">Field name to match patterns against</param>
    /// <returns>Debug information about the generated patterns and query</returns>
    [HttpGet("times/echo")]
    [NightscoutEndpoint("/api/v1/times/echo")]
    [ProducesResponseType(typeof(TimeQueryEcho), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public ActionResult<TimeQueryEcho> GetTimeQueryEcho(
        [FromQuery] string storage = "entries",
        [FromQuery] string field = "dateString"
    )
    {
        if (RefuseUnsupportedStorage(storage) is { } unsupported) return unsupported;
        if (LegacyStorageReadScopes.RefuseRead(HttpContext, storage) is { } refusal) return refusal;

        return GetTimeQueryEchoInternal(null, null, storage, field);
    }

    /// <summary>
    /// Time query debugging and pattern inspection with prefix
    /// Shows how the time patterns are expanded and what MongoDB query would be generated
    /// </summary>
    /// <param name="prefix">Time prefix pattern</param>
    /// <param name="storage">Storage type (entries, treatments, devicestatus)</param>
    /// <param name="field">Field name to match patterns against</param>
    /// <returns>Debug information about the generated patterns and query</returns>
    [HttpGet("times/echo/{prefix}")]
    [NightscoutEndpoint("/api/v1/times/echo/{prefix}")]
    [ProducesResponseType(typeof(TimeQueryEcho), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public ActionResult<TimeQueryEcho> GetTimeQueryEchoWithPrefix(
        string prefix,
        [FromQuery] string storage = "entries",
        [FromQuery] string field = "dateString"
    )
    {
        if (RefuseUnsupportedStorage(storage) is { } unsupported) return unsupported;
        if (LegacyStorageReadScopes.RefuseRead(HttpContext, storage) is { } refusal) return refusal;

        return GetTimeQueryEchoInternal(prefix, null, storage, field);
    }

    /// <summary>
    /// Time query debugging and pattern inspection with prefix and regex
    /// Shows how the time patterns are expanded and what MongoDB query would be generated
    /// </summary>
    /// <param name="prefix">Time prefix pattern</param>
    /// <param name="regex">Time regex pattern</param>
    /// <param name="storage">Storage type (entries, treatments, devicestatus)</param>
    /// <param name="field">Field name to match patterns against</param>
    /// <returns>Debug information about the generated patterns and query</returns>
    [HttpGet("times/echo/{prefix}/{regex}")]
    [NightscoutEndpoint("/api/v1/times/echo/{prefix}/{regex}")]
    [ProducesResponseType(typeof(TimeQueryEcho), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public ActionResult<TimeQueryEcho> GetTimeQueryEchoWithPrefixAndRegex(
        string prefix,
        string regex,
        [FromQuery] string storage = "entries",
        [FromQuery] string field = "dateString"
    )
    {
        if (RefuseUnsupportedStorage(storage) is { } unsupported) return unsupported;
        if (LegacyStorageReadScopes.RefuseRead(HttpContext, storage) is { } refusal) return refusal;

        return GetTimeQueryEchoInternal(prefix, regex, storage, field);
    }

    private ActionResult<TimeQueryEcho> GetTimeQueryEchoInternal(
        string? prefix = null,
        string? regex = null,
        string storage = "entries",
        string field = "dateString"
    )
    {
        _logger.LogDebug(
            "Time query echo requested - prefix: {Prefix}, regex: {Regex}, storage: {Storage}, field: {Field} from {RemoteIpAddress}",
            prefix,
            regex,
            storage,
            field,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Generate debug information
            var echo = _timeQueryService.GenerateTimeQueryEcho(
                prefix: prefix,
                regex: regex,
                storage: storage,
                fieldName: field,
                queryString: Request.QueryString.Value
            );

            _logger.LogDebug(
                "Successfully generated time query echo with {PatternCount} patterns",
                echo.Pattern.Count()
            );

            return Ok(echo);
        }
        catch (TimePatternRejectedException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error generating time query echo with prefix: {Prefix}, regex: {Regex}",
                prefix,
                regex
            );
            return StatusCode(
                500,
                new
                {
                    status = 500,
                    message = "Internal server error",
                    type = "internal",
                    error = ex.Message,
                }
            );
        }
    }

    /// <summary>
    /// Advanced data slicing with regex patterns and field filtering
    /// Allows specifying storage layer, field to match, entry type, and pattern matching
    /// <example>
    /// - /api/v1/slice/entries/dateString - All entries matching dateString field
    /// - /api/v1/slice/entries/dateString/mbg/2015 - All MBG entries from 2015
    /// - /api/v1/slice/treatments/created_at/bolus/2015-04 - All bolus treatments from April 2015
    /// </example>
    /// </summary>
    /// <param name="storage">Storage type (entries, treatments, devicestatus)</param>
    /// <param name="field">Field to perform pattern matching on</param>
    /// <param name="count">Maximum number of entries to return (default: 10)</param>
    /// <param name="format">Output format (json, csv, tsv, txt)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Entries matching the slice criteria</returns>
    [HttpGet("slice/{storage}/{field}")]
    [NightscoutEndpoint("/api/v1/slice/{storage}/{field}")]
    [ProducesResponseType(typeof(Entry[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetSlicedData(
        string storage,
        string field,
        [FromQuery] int count = 10,
        [FromQuery] string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        if (RefuseUnsupportedStorage(storage) is { } unsupported) return unsupported;
        if (LegacyStorageReadScopes.RefuseRead(HttpContext, storage) is { } refusal) return refusal;

        return await GetSlicedDataInternal(
            storage,
            field,
            null,
            null,
            null,
            count,
            format,
            cancellationToken
        );
    }

    /// <summary>
    /// Advanced data slicing with type filter
    /// </summary>
    /// <param name="storage">Storage type (entries, treatments, devicestatus)</param>
    /// <param name="field">Field to perform pattern matching on</param>
    /// <param name="type">Entry/record type filter</param>
    /// <param name="count">Maximum number of entries to return (default: 10)</param>
    /// <param name="format">Output format (json, csv, tsv, txt)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Entries matching the slice criteria</returns>
    [HttpGet("slice/{storage}/{field}/{type}")]
    [NightscoutEndpoint("/api/v1/slice/{storage}/{field}/{type}")]
    [ProducesResponseType(typeof(Entry[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetSlicedDataWithType(
        string storage,
        string field,
        string type,
        [FromQuery] int count = 10,
        [FromQuery] string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        if (RefuseUnsupportedStorage(storage) is { } unsupported) return unsupported;
        if (LegacyStorageReadScopes.RefuseRead(HttpContext, storage) is { } refusal) return refusal;

        return await GetSlicedDataInternal(
            storage,
            field,
            type,
            null,
            null,
            count,
            format,
            cancellationToken
        );
    }

    /// <summary>
    /// Advanced data slicing with type and prefix filter
    /// </summary>
    /// <param name="storage">Storage type (entries, treatments, devicestatus)</param>
    /// <param name="field">Field to perform pattern matching on</param>
    /// <param name="type">Entry/record type filter</param>
    /// <param name="prefix">Pattern prefix</param>
    /// <param name="count">Maximum number of entries to return (default: 10)</param>
    /// <param name="format">Output format (json, csv, tsv, txt)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Entries matching the slice criteria</returns>
    [HttpGet("slice/{storage}/{field}/{type}/{prefix}")]
    [NightscoutEndpoint("/api/v1/slice/{storage}/{field}/{type}/{prefix}")]
    [ProducesResponseType(typeof(Entry[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetSlicedDataWithTypeAndPrefix(
        string storage,
        string field,
        string type,
        string prefix,
        [FromQuery] int count = 10,
        [FromQuery] string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        if (RefuseUnsupportedStorage(storage) is { } unsupported) return unsupported;
        if (LegacyStorageReadScopes.RefuseRead(HttpContext, storage) is { } refusal) return refusal;

        return await GetSlicedDataInternal(
            storage,
            field,
            type,
            prefix,
            null,
            count,
            format,
            cancellationToken
        );
    }

    /// <summary>
    /// Advanced data slicing with all parameters
    /// </summary>
    /// <param name="storage">Storage type (entries, treatments, devicestatus)</param>
    /// <param name="field">Field to perform pattern matching on</param>
    /// <param name="type">Entry/record type filter</param>
    /// <param name="prefix">Pattern prefix</param>
    /// <param name="regex">Pattern regex</param>
    /// <param name="count">Maximum number of entries to return (default: 10)</param>
    /// <param name="format">Output format (json, csv, tsv, txt)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Entries matching the slice criteria</returns>
    [HttpGet("slice/{storage}/{field}/{type}/{prefix}/{regex}")]
    [NightscoutEndpoint("/api/v1/slice/{storage}/{field}/{type}/{prefix}/{regex}")]
    [ProducesResponseType(typeof(Entry[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetSlicedDataWithAll(
        string storage,
        string field,
        string type,
        string prefix,
        string regex,
        [FromQuery] int count = 10,
        [FromQuery] string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        if (RefuseUnsupportedStorage(storage) is { } unsupported) return unsupported;
        if (LegacyStorageReadScopes.RefuseRead(HttpContext, storage) is { } refusal) return refusal;

        return await GetSlicedDataInternal(
            storage,
            field,
            type,
            prefix,
            regex,
            count,
            format,
            cancellationToken
        );
    }

    private async Task<ActionResult> GetSlicedDataInternal(
        string storage,
        string field,
        string? type = null,
        string? prefix = null,
        string? regex = null,
        int count = 10,
        string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Slice query requested - storage: {Storage}, field: {Field}, type: {Type}, prefix: {Prefix}, regex: {Regex}, count: {Count} from {RemoteIpAddress}",
            storage,
            field,
            type,
            prefix,
            regex,
            count,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Validate parameters
            if (count < 0)
            {
                _logger.LogWarning("Invalid count parameter: {Count}", count);
                return BadRequest($"Count must be non-negative, got {count}");
            }

            if (string.IsNullOrWhiteSpace(storage) || string.IsNullOrWhiteSpace(field))
            {
                _logger.LogWarning("Storage and field parameters are required");
                return BadRequest("Storage and field parameters are required");
            }

            if (!SafeFieldName.IsMatch(field))
            {
                _logger.LogWarning("Invalid field parameter: {Field}", field);
                return BadRequest("Field must be a field name made of letters, digits, '_' and '.'");
            }

            // Execute slice query
            var entries = await _timeQueryService.ExecuteSliceQueryAsync(
                storage: storage,
                field: field,
                type: type,
                prefix: prefix,
                regex: regex,
                find: FindParameters(),
                cancellationToken: cancellationToken
            );

            var entriesArray = entries.Take(count).ToArray();

            _logger.LogDebug(
                "Successfully returned {Count} entries for slice query",
                entriesArray.Length
            );

            return EntriesResult(entriesArray, format);
        }
        catch (TimePatternRejectedException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error executing slice query - storage: {Storage}, field: {Field}, type: {Type}, prefix: {Prefix}, regex: {Regex}",
                storage,
                field,
                type,
                prefix,
                regex
            );
            return StatusCode(
                500,
                new
                {
                    status = 500,
                    message = "Internal server error",
                    type = "internal",
                    error = ex.Message,
                }
            );
        }
    }

    private ActionResult EntriesResult(Entry[] entries, string? format) =>
        this.EntriesText(entries, format) ?? Ok(entries);

    /// <summary>
    /// The request's <c>find[...]</c> pairs alone, so no other key (<c>token</c>, <c>secret</c>, a
    /// cache buster) reaches the read services or anything they key on.
    /// </summary>
    private string FindParameters() =>
        QueryString.Create(
            Request.Query.Where(q => q.Key.StartsWith("find[", StringComparison.OrdinalIgnoreCase))
        ).Value?.TrimStart('?') ?? "";

    /// <summary>
    /// Refuses a collection these routes do not dispatch on. Checked ahead of the scope gate so the
    /// answer is the same whatever the caller holds, rather than 403 for one grant and 400 for
    /// another on a collection the route never served.
    /// </summary>
    private ActionResult? RefuseUnsupportedStorage(string storage)
    {
        if (SliceableStorage.Contains(storage, StringComparer.OrdinalIgnoreCase))
            return null;

        _logger.LogWarning("Invalid storage type requested: {Storage}", storage);
        return BadRequest($"Storage must be one of: {string.Join(", ", SliceableStorage)}");
    }
}
