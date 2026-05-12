using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Models;


namespace Nocturne.API.Controllers.V4.Platform;

/// <summary>
/// Controller for debug and query inspection endpoints
/// Provides query debugging and MongoDB query inspection capabilities
/// </summary>
[ApiController]
[Tags("Platform")]
[Route("api/v4/debug")]
[Produces("application/json")]
[Authorize]
public class DebugController : ControllerBase
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DebugController> _logger;

    /// <summary>
    /// Initializes a new instance of the DebugController
    /// </summary>
    /// <param name="environment">Web host environment</param>
    /// <param name="logger">Logger instance</param>
    public DebugController(
        IWebHostEnvironment environment,
        ILogger<DebugController> logger
    )
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Echo endpoint for debugging MongoDB queries
    /// Returns information about how REST API parameters translate into MongoDB queries
    /// </summary>
    /// <param name="echo">Storage type to query (entries, treatments, devicestatus, activity)</param>
    /// <returns>Query debugging information</returns>
    /// <response code="200">Query information returned successfully</response>
    /// <response code="400">Invalid parameters</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("echo/{echo}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult<object> EchoQuery(string echo)
    {
        return EchoQueryInternal(echo, null, null);
    }

    /// <summary>
    /// Echo endpoint for debugging MongoDB queries with model
    /// Returns information about how REST API parameters translate into MongoDB queries
    /// </summary>
    /// <param name="echo">Storage type to query (entries, treatments, devicestatus, activity)</param>
    /// <param name="model">Model specification</param>
    /// <returns>Query debugging information</returns>
    /// <response code="200">Query information returned successfully</response>
    /// <response code="400">Invalid parameters</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("echo/{echo}/{model}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult<object> EchoQueryWithModel(string echo, string model)
    {
        return EchoQueryInternal(echo, model, null);
    }

    /// <summary>
    /// Echo endpoint for debugging MongoDB queries with model and spec
    /// Returns information about how REST API parameters translate into MongoDB queries
    /// </summary>
    /// <param name="echo">Storage type to query (entries, treatments, devicestatus, activity)</param>
    /// <param name="model">Model specification</param>
    /// <param name="spec">Specification parameter</param>
    /// <returns>Query debugging information</returns>
    /// <response code="200">Query information returned successfully</response>
    /// <response code="400">Invalid parameters</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("echo/{echo}/{model}/{spec}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult<object> EchoQueryWithModelAndSpec(string echo, string model, string spec)
    {
        return EchoQueryInternal(echo, model, spec);
    }

    private ActionResult<object> EchoQueryInternal(
        string echo,
        string? model = null,
        string? spec = null
    )
    {
        try
        {
            _logger.LogDebug(
                "Echo query for storage: {Echo}, model: {Model}, spec: {Spec}",
                echo,
                model,
                spec
            );

            // Extract query parameters from the request
            var queryParams = new Dictionary<string, object>();
            foreach (var param in Request.Query)
            {
                if (param.Value.Count == 1)
                {
                    queryParams[param.Key] = param.Value.ToString();
                }
                else
                {
                    queryParams[param.Key] = param.Value.ToArray();
                }
            }

            // Default count if not specified
            if (!queryParams.ContainsKey("count"))
            {
                queryParams["count"] = "10";
            }

            // Validate storage type
            var validStorageTypes = new[]
            {
                "entries",
                "treatments",
                "devicestatus",
                "activity",
                "profile",
                "food",
            };
            if (!validStorageTypes.Contains(echo.ToLowerInvariant()))
            {
                return BadRequest(
                    new
                    {
                        error = $"Invalid storage type: {echo}. Valid types are: {string.Join(", ", validStorageTypes)}",
                    }
                );
            }

            // Build the response with query information
            var response = new
            {
                query = BuildMongoQuery(queryParams, echo),
                input = queryParams,
                @params = new
                {
                    echo = echo,
                    model = model,
                    spec = spec,
                },
                storage = echo,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                queryString = Request.QueryString.ToString(),
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing echo query for storage: {Storage}", echo);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "An error occurred while processing the echo query" }
            );
        }
    }

    /// <summary>
    /// Preview endpoint for entry creation without persistence
    /// Allows previewing entry data without actually storing it in the database
    /// </summary>
    /// <param name="entries">Entry data to preview (single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Echoed entry data with validation results</returns>
    /// <response code="200">Entry data previewed successfully</response>
    /// <response code="400">Invalid entry data</response>
    /// <response code="500">Internal server error</response>
    [HttpPost("entries/preview")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public Task<ActionResult<object>> PreviewEntries(
        [FromBody] object entries,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Previewing entries without persistence");

            if (entries == null)
            {
                return Task.FromResult<ActionResult<object>>(
                    Problem(detail: "Entry data is required", statusCode: 400, title: "Bad Request")
                );
            }

            List<Entry> entryList;
            var validationResults = new List<object>();

            // Handle both single entry and array of entries
            if (entries is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.Array)
                {
                    try
                    {
                        entryList =
                            JsonSerializer.Deserialize<List<Entry>>(jsonElement.GetRawText())
                            ?? new List<Entry>();
                    }
                    catch (JsonException ex)
                    {
                        return Task.FromResult<ActionResult<object>>(
                            BadRequest(
                                new
                                {
                                    error = "Invalid JSON format for entry array",
                                    details = ex.Message,
                                }
                            )
                        );
                    }
                }
                else
                {
                    try
                    {
                        var singleEntry = JsonSerializer.Deserialize<Entry>(
                            jsonElement.GetRawText()
                        );
                        entryList =
                            singleEntry != null
                                ? new List<Entry> { singleEntry }
                                : new List<Entry>();
                    }
                    catch (JsonException ex)
                    {
                        return Task.FromResult<ActionResult<object>>(
                            BadRequest(
                                new
                                {
                                    error = "Invalid JSON format for entry",
                                    details = ex.Message,
                                }
                            )
                        );
                    }
                }
            }
            else
            {
                return Task.FromResult<ActionResult<object>>(
                    Problem(detail: "Invalid entry data format", statusCode: 400, title: "Bad Request")
                );
            }

            // Validate each entry and build validation results
            foreach (var entry in entryList)
            {
                var validation = ValidateEntry(entry);
                validationResults.Add(
                    new
                    {
                        entry = entry,
                        validation = validation,
                        preview = true,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }

            var response = new
            {
                entries = entryList,
                validationResults = validationResults,
                summary = new
                {
                    totalEntries = entryList.Count,
                    validEntries = validationResults.Count(v => ((dynamic)v).validation.isValid),
                    invalidEntries = validationResults.Count(v => !((dynamic)v).validation.isValid),
                    preview = true,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            };

            return Task.FromResult<ActionResult<object>>(Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error previewing entries");
            return Task.FromResult<ActionResult<object>>(
                StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new { error = "An error occurred while previewing entries" }
                )
            );
        }
    }

    /// <summary>
    /// Builds a MongoDB query representation from query parameters
    /// </summary>
    /// <param name="queryParams">Query parameters from the request</param>
    /// <param name="storageType">Storage type (entries, treatments, etc.)</param>
    /// <returns>MongoDB query representation</returns>
    private object BuildMongoQuery(Dictionary<string, object> queryParams, string storageType)
    {
        var query = new Dictionary<string, object>();

        // Handle 'find' parameter which contains MongoDB query filters
        if (queryParams.ContainsKey("find") && queryParams["find"] is string findParam)
        {
            try
            {
                // Parse the find parameter as JSON
                var findQuery = JsonSerializer.Deserialize<Dictionary<string, object>>(findParam);
                if (findQuery != null)
                {
                    foreach (var kvp in findQuery)
                    {
                        query[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (JsonException)
            {
                // If parsing fails, treat as a simple key-value filter
                query["find"] = findParam;
            }
        }

        // Handle other common query parameters
        foreach (var param in queryParams)
        {
            switch (param.Key.ToLowerInvariant())
            {
                case "count":
                case "limit":
                    if (int.TryParse(param.Value.ToString(), out var countValue))
                    {
                        query["$limit"] = countValue;
                    }
                    break;
                case "skip":
                case "offset":
                    if (int.TryParse(param.Value.ToString(), out var skipValue))
                    {
                        query["$skip"] = skipValue;
                    }
                    break;
                case "sort":
                    query["$sort"] = param.Value;
                    break;
                case "datestring":
                    query["dateString"] = new Dictionary<string, object>
                    {
                        { "$regex", param.Value },
                    };
                    break;
                case "type":
                    query["type"] = param.Value;
                    break;
                default:
                    if (!param.Key.Equals("find", StringComparison.OrdinalIgnoreCase))
                    {
                        query[param.Key] = param.Value;
                    }
                    break;
            }
        } // Add default sort if not specified
        if (!query.ContainsKey("$sort"))
        {
            switch (storageType.ToLowerInvariant())
            {
                case "entries":
                    query["$sort"] = new Dictionary<string, object> { { "date", -1 } };
                    break;
                case "treatments":
                case "activity":
                    query["$sort"] = new Dictionary<string, object> { { "created_at", -1 } };
                    break;
                case "devicestatus":
                    query["$sort"] = new Dictionary<string, object> { { "mills", -1 } };
                    break;
                default:
                    query["$sort"] = new Dictionary<string, object> { { "_id", -1 } };
                    break;
            }
        }

        return query;
    }

    /// <summary>
    /// Validates an entry for preview purposes
    /// </summary>
    /// <param name="entry">Entry to validate</param>
    /// <returns>Validation result</returns>
    private object ValidateEntry(Entry entry)
    {
        var errors = new List<string>();
        var warnings = new List<string>(); // Basic validation
        if (entry.Sgv == null && entry.Mgdl == 0)
        {
            errors.Add("Entry must have either 'sgv' or 'mgdl' value");
        }

        if (entry.Sgv.HasValue && (entry.Sgv < 0 || entry.Sgv > 1000))
        {
            warnings.Add("SGV value seems out of normal range (0-1000)");
        }

        if (entry.Mgdl > 0 && (entry.Mgdl < 0 || entry.Mgdl > 1000))
        {
            warnings.Add("MGDL value seems out of normal range (0-1000)");
        }

        if (string.IsNullOrEmpty(entry.Type))
        {
            warnings.Add("Entry type is not specified");
        }

        if (entry.Mills == 0 && string.IsNullOrEmpty(entry.DateString))
        {
            errors.Add("Entry must have either 'mills' timestamp or 'dateString'");
        }

        return new
        {
            isValid = errors.Count == 0,
            errors = errors,
            warnings = warnings,
            fieldCount = CountNonNullFields(entry),
        };
    }

    /// <summary>
    /// Counts non-null fields in an entry
    /// </summary>
    /// <param name="entry">Entry to analyze</param>
    /// <returns>Number of non-null fields</returns>
    private int CountNonNullFields(Entry entry)
    {
        var count = 0;
        if (!string.IsNullOrEmpty(entry.Id))
            count++;
        if (entry.Mills > 0)
            count++;
        if (entry.Date.HasValue)
            count++;
        if (!string.IsNullOrEmpty(entry.DateString))
            count++;
        if (entry.Sgv.HasValue)
            count++;
        if (entry.Mgdl > 0)
            count++;
        if (!string.IsNullOrEmpty(entry.Type))
            count++;
        if (!string.IsNullOrEmpty(entry.Direction))
            count++;
        if (entry.Noise.HasValue)
            count++;
        if (entry.Filtered.HasValue)
            count++;
        if (entry.Unfiltered.HasValue)
            count++;
        if (entry.Rssi.HasValue)
            count++;
        if (!string.IsNullOrEmpty(entry.Device))
            count++;
        return count;
    }

}
