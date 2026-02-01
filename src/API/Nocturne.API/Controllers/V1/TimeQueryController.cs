using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Time query controller that provides 1:1 compatibility with Nightscout time-based endpoints
/// Supports bash-style brace expansion for complex time pattern matching
/// </summary>
[ApiController]
[Route("api/v1")]
public class TimeQueryController : ControllerBase
{
    private readonly ITimeQueryService _timeQueryService;
    private readonly IDataFormatService _dataFormatService;
    private readonly ILogger<TimeQueryController> _logger;

    public TimeQueryController(
        ITimeQueryService timeQueryService,
        IDataFormatService dataFormatService,
        ILogger<TimeQueryController> logger
    )
    {
        _timeQueryService = timeQueryService;
        _dataFormatService = dataFormatService;
        _logger = logger;
    }

    /// <summary>
    /// /api/v1/times without prefix is not a valid Nightscout endpoint.
    /// Nightscout returns 404 for this path. We match that behavior for parity.
    /// </summary>
    /// <returns>404 Not Found to match Nightscout behavior</returns>
    [HttpGet("times")]
    [ApiExplorerSettings(IgnoreApi = true)] // Hide from OpenAPI as it's not a real endpoint
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

            // Extract query parameters for additional filtering
            var queryParameters = new Dictionary<string, object>();
            foreach (var param in Request.Query)
            {
                if (param.Key != "count" && param.Key != "format")
                {
                    queryParameters[param.Key] = param.Value.ToString() ?? "";
                }
            }

            // Execute time-based query
            var entries = await _timeQueryService.ExecuteTimeQueryAsync(
                prefix: prefix,
                regex: regex,
                storage: "entries",
                fieldName: "dateString",
                queryParameters: queryParameters,
                cancellationToken: cancellationToken
            );

            var entriesArray = entries.Take(count).ToArray();

            _logger.LogDebug(
                "Successfully returned {Count} entries for time query",
                entriesArray.Length
            );

            // Handle different output formats
            if (format != null && !format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var formattedData = _dataFormatService.FormatEntries(entriesArray, format);
                    var contentType = _dataFormatService.GetContentType(format);
                    return Content(formattedData, contentType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to format data as {Format}, falling back to JSON",
                        format
                    );
                    return Ok(entriesArray);
                }
            }

            return Ok(entriesArray);
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
            // Extract query parameters
            var queryParameters = new Dictionary<string, object>();
            foreach (var param in Request.Query)
            {
                if (param.Key != "storage" && param.Key != "field")
                {
                    queryParameters[param.Key] = param.Value.ToString() ?? "";
                }
            }

            // Generate debug information
            var echo = _timeQueryService.GenerateTimeQueryEcho(
                prefix: prefix,
                regex: regex,
                storage: storage,
                fieldName: field,
                queryParameters: queryParameters
            );

            _logger.LogDebug(
                "Successfully generated time query echo with {PatternCount} patterns",
                echo.Pattern.Count()
            );

            return Ok(echo);
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

            // Validate storage type
            var validStorageTypes = new[] { "entries", "treatments", "devicestatus" };
            if (!validStorageTypes.Contains(storage.ToLowerInvariant()))
            {
                _logger.LogWarning("Invalid storage type: {Storage}", storage);
                return BadRequest(
                    $"Storage must be one of: {string.Join(", ", validStorageTypes)}"
                );
            }

            // Extract query parameters for additional filtering
            var queryParameters = new Dictionary<string, object>();
            foreach (var param in Request.Query)
            {
                if (param.Key != "count" && param.Key != "format")
                {
                    queryParameters[param.Key] = param.Value.ToString() ?? "";
                }
            }

            // Execute slice query
            var entries = await _timeQueryService.ExecuteSliceQueryAsync(
                storage: storage,
                field: field,
                type: type,
                prefix: prefix,
                regex: regex,
                queryParameters: queryParameters,
                cancellationToken: cancellationToken
            );

            var entriesArray = entries.Take(count).ToArray();

            _logger.LogDebug(
                "Successfully returned {Count} entries for slice query",
                entriesArray.Length
            );

            // Handle different output formats
            if (format != null && !format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var formattedData = _dataFormatService.FormatEntries(entriesArray, format);
                    var contentType = _dataFormatService.GetContentType(format);
                    return Content(formattedData, contentType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to format data as {Format}, falling back to JSON",
                        format
                    );
                    return Ok(entriesArray);
                }
            }

            return Ok(entriesArray);
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
}
