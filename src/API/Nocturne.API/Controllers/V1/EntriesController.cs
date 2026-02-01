using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Extensions;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Entries controller that provides 1:1 compatibility with Nightscout entries endpoints
/// Implements the /api/v1/entries/* endpoints from the legacy JavaScript implementation
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class EntriesController : ControllerBase
{
    private readonly IEntryService _entryService;
    private readonly IDataFormatService _dataFormatService;
    private readonly IDocumentProcessingService _documentProcessingService;
    private readonly IProcessingStatusService _processingStatusService;
    private readonly IAlertOrchestrator _alertOrchestrator;
    private readonly ILogger<EntriesController> _logger;

    public EntriesController(
        IEntryService entryService,
        IDataFormatService dataFormatService,
        IDocumentProcessingService documentProcessingService,
        IProcessingStatusService processingStatusService,
        IAlertOrchestrator alertOrchestrator,
        ILogger<EntriesController> logger
    )
    {
        _entryService = entryService;
        _dataFormatService = dataFormatService;
        _documentProcessingService = documentProcessingService;
        _processingStatusService = processingStatusService;
        _alertOrchestrator = alertOrchestrator;
        _logger = logger;
    }

    /// <summary>
    /// Get the most recent glucose entry
    /// This endpoint assumes SGV (sensor glucose value) type and returns the single most recent entry
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The most recent glucose entry, or empty array if no entries exist</returns>
    [HttpGet("current")]
    [NightscoutEndpoint("/api/v1/entries/current")]
    [ProducesResponseType(typeof(Entry[]), 200)]
    [ProducesResponseType(typeof(Entry[]), 304)] // Not Modified response
    public async Task<ActionResult<Entry[]>> GetCurrentEntry(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Current entry endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var currentEntry = await _entryService.GetCurrentEntryAsync(cancellationToken);

            // Set Last-Modified header for caching
            DateTimeOffset lastModified;
            if (currentEntry == null)
            {
                _logger.LogDebug("No current entry found, returning empty array");
                // Set Last-Modified to current time when no entries exist
                lastModified = DateTimeOffset.UtcNow;
                Response.Headers["Last-Modified"] = lastModified.ToString("R");
                return Ok(Array.Empty<Entry>());
            }
            lastModified = DateTimeOffset.FromUnixTimeMilliseconds(currentEntry.Mills);
            Response.Headers["Last-Modified"] = lastModified.ToString("R");

            // Check If-Modified-Since header
            if (Request.Headers.IfModifiedSince.Count > 0)
            {
                if (
                    DateTimeOffset.TryParse(
                        Request.Headers.IfModifiedSince.First(),
                        out var ifModifiedSince
                    )
                )
                {
                    if (lastModified <= ifModifiedSince)
                    {
                        _logger.LogDebug(
                            "Current entry not modified since {IfModifiedSince}, returning 304",
                            ifModifiedSince
                        );
                        return StatusCode(
                            304,
                            new
                            {
                                status = 304,
                                message = "Not modified",
                                type = "internal",
                            }
                        );
                    }
                }
            }

            _logger.LogDebug(
                "Returning current entry with ID: {EntryId}, Mills: {Mills}, SGV: {Sgv}",
                currentEntry.Id,
                currentEntry.Mills,
                currentEntry.Sgv ?? currentEntry.Mgdl
            );

            // Return as array to match legacy API format with V1 response structure
            return Ok(new[] { currentEntry }.ToV1Responses());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving current entry");

            // Return error response in legacy format
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
    /// Get a specific entry by ID or get entries by type
    /// </summary>
    /// <param name="spec">Either an entry ID (24-character hex string) or entry type (e.g., "sgv", "mbg", "cal")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Entry or entries matching the specification</returns>
    [HttpGet("{spec}")]
    [NightscoutEndpoint("/api/v1/entries/{spec}")]
    [ProducesResponseType(typeof(Entry[]), 200)]
    [ProducesResponseType(typeof(Entry[]), 304)] // Not Modified response
    public async Task<ActionResult<Entry[]>> GetEntry(
        string spec,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Entry spec endpoint requested with spec: {Spec} from {RemoteIpAddress}",
            spec,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Check if spec is a 24-character hex string (MongoDB ObjectId)
            bool isId =
                spec.Length == 24
                && System.Text.RegularExpressions.Regex.IsMatch(
                    spec,
                    "^[a-f\\d]{24}$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );

            if (isId)
            {
                // Fetch specific entry by ID
                var entry = await _entryService.GetEntryByIdAsync(spec, cancellationToken);
                if (entry == null)
                {
                    _logger.LogDebug("Entry with ID {Id} not found", spec);
                    // Set Last-Modified to current time when entry not found
                    var notFoundLastModified = DateTimeOffset.UtcNow;
                    Response.Headers["Last-Modified"] = notFoundLastModified.ToString("R");
                    return Ok(Array.Empty<Entry>());
                }

                _logger.LogDebug("Found entry with ID: {Id}", spec);
                // Set Last-Modified header
                var lastModified = DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills);
                Response.Headers["Last-Modified"] = lastModified.ToString("R");

                return Ok(new[] { entry }.ToV1Responses());
            }
            else
            {
                // Treat spec as entry type (e.g., "sgv", "mbg", "cal")
                _logger.LogDebug("Fetching entries of type: {Type}", spec);
                var entries = await _entryService.GetEntriesAsync(
                    type: spec,
                    count: 10,
                    skip: 0,
                    cancellationToken
                );
                var entriesArray = entries.ToArray();

                // Set Last-Modified header for caching
                DateTimeOffset lastModified;
                if (entriesArray.Length > 0)
                {
                    // Set Last-Modified header based on most recent entry
                    lastModified = DateTimeOffset.FromUnixTimeMilliseconds(entriesArray[0].Mills);
                }
                else
                {
                    // Set Last-Modified to current time when no entries exist
                    lastModified = DateTimeOffset.UtcNow;
                }
                Response.Headers["Last-Modified"] = lastModified.ToString("R");

                // Check If-Modified-Since header
                if (Request.Headers.IfModifiedSince.Count > 0)
                {
                    if (
                        DateTimeOffset.TryParse(
                            Request.Headers.IfModifiedSince.First(),
                            out var ifModifiedSince
                        )
                    )
                    {
                        if (lastModified <= ifModifiedSince)
                        {
                            _logger.LogDebug(
                                "Entries not modified since {IfModifiedSince}, returning 304",
                                ifModifiedSince
                            );
                            return StatusCode(
                                304,
                                new
                                {
                                    status = 304,
                                    message = "Not modified",
                                    type = "internal",
                                }
                            );
                        }
                    }
                }

                _logger.LogDebug(
                    "Found {Count} entries of type: {Type}",
                    entriesArray.Length,
                    spec
                );
                return Ok(entriesArray.ToV1Responses());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving entry with spec: {Spec}", spec);

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
    /// Get entries with optional query parameters
    /// Supports advanced query features including find filters, date ranges, and pagination
    /// </summary>
    /// <param name="count">Maximum number of entries to return (if not specified, returns all matching entries)</param>
    /// <param name="type">Entry type filter (default: "sgv")</param>
    /// <param name="find">MongoDB-style find query filters (JSON format) - for unit tests</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="dateString">ISO date string for date filtering</param>
    /// <param name="rr">Reverse results (latest first)</param>
    /// <param name="format">Output format (json, csv, tsv, txt)</param>
    /// <returns>Array of entries matching the criteria</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v1/entries")]
    [ProducesResponseType(typeof(Entry[]), 200)]
    [ProducesResponseType(typeof(Entry[]), 304)] // Not Modified response
    public async Task<ActionResult> GetEntries(
        [FromQuery] string? find = null,
        [FromQuery] int? count = null,
        [FromQuery] string? dateString = null,
        [FromQuery] string? type = null,
        [FromQuery] int rr = 0,
        [FromQuery] string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        // Get the full query string to handle multiple find parameters correctly
        var queryString = HttpContext?.Request?.QueryString.ToString() ?? string.Empty;

        // Strip the leading '?' if present
        if (queryString.StartsWith("?"))
        {
            queryString = queryString.Substring(1);
        }

        // DEBUG: Log the query string details
        _logger.LogDebug("Processing query string: '{QueryString}'", queryString);

        // Extract find query from the query string (handles multiple find parameters)
        // Use query string if it contains find parameters, otherwise use the find parameter for unit tests
        string? findQuery = null;
        if (
            !string.IsNullOrEmpty(queryString)
            && (queryString.Contains("find[") || queryString.Contains("find%5B"))
        )
        {
            findQuery = queryString;
        }
        else if (!string.IsNullOrEmpty(find))
        {
            findQuery = find;
        }

        _logger.LogInformation(
            "Entries endpoint requested with count: {Count}, type: {Type}, findQuery: {FindQuery}, dateString: {DateString}, rr: {RR}, format: {Format} from {RemoteIpAddress}",
            count,
            type,
            findQuery,
            dateString,
            rr,
            format,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // In Nightscout v1, the ?type= parameter does NOT filter by entry type
            // It may be related to output format. To filter by type, use find[type]=xxx
            // Only apply type filtering when it comes from find query, not from ?type= parameter
            string? entryType = null;

            // Check if find query contains type filter
            if (
                !string.IsNullOrEmpty(findQuery)
                && (findQuery.Contains("find[type]") || findQuery.Contains("find%5Btype%5D"))
            )
            {
                // Type filtering will be handled by the find query parser
                entryType = null;
            }

            // Handle count parameter for Nightscout compatibility:
            // - null/not specified: default to 10 (Nightscout default)
            // - 0 or negative: return empty array (Nightscout behavior)
            // - positive: return that many entries
            if (count.HasValue && count.Value <= 0)
            {
                // Nightscout returns empty array for count=0 or negative values
                return Ok(Array.Empty<Entry>());
            }
            // Nightscout defaults to 10 when count is not specified
            var limitedCount = count ?? 10;

            // Convert rr parameter to boolean (non-zero means reverse)
            var reverseResults = rr != 0;

            // Use advanced filtering if any advanced parameters are provided
            var entries = await _entryService.GetEntriesWithAdvancedFilterAsync(
                type: entryType,
                count: limitedCount,
                skip: 0,
                findQuery: findQuery,
                dateString: dateString,
                reverseResults: reverseResults,
                cancellationToken: cancellationToken
            );
            var entriesArray = entries.ToArray();

            // Set Last-Modified header for caching
            DateTimeOffset lastModified;
            if (entriesArray.Length > 0)
            {
                // Set Last-Modified header based on most recent entry
                lastModified = DateTimeOffset.FromUnixTimeMilliseconds(entriesArray[0].Mills);
            }
            else
            {
                // Set Last-Modified to current time when no entries exist
                lastModified = DateTimeOffset.UtcNow;
            }
            Response.Headers["Last-Modified"] = lastModified.ToString("R");

            // Check If-Modified-Since header
            if (Request.Headers.IfModifiedSince.Count > 0)
            {
                if (
                    DateTimeOffset.TryParse(
                        Request.Headers.IfModifiedSince.First(),
                        out var ifModifiedSince
                    )
                )
                {
                    if (lastModified <= ifModifiedSince)
                    {
                        _logger.LogDebug(
                            "Entries not modified since {IfModifiedSince}, returning 304",
                            ifModifiedSince
                        );
                        return StatusCode(
                            304,
                            new
                            {
                                status = 304,
                                message = "Not modified",
                                type = "internal",
                            }
                        );
                    }
                }
            }
            _logger.LogDebug(
                "Found {Count} entries of type: {Type}",
                entriesArray.Length,
                entryType
            );

            // Determine format from format parameter or Accept header (content negotiation)
            var effectiveFormat = format;
            if (
                string.IsNullOrEmpty(effectiveFormat)
                || effectiveFormat.Equals("json", StringComparison.OrdinalIgnoreCase)
            )
            {
                // Check Accept header for content negotiation (Nightscout compatibility)
                var acceptHeader = Request.Headers.Accept.ToString().ToLowerInvariant();
                if (acceptHeader.Contains("text/tab-separated-values"))
                {
                    effectiveFormat = "tsv";
                }
                else if (acceptHeader.Contains("text/csv"))
                {
                    effectiveFormat = "csv";
                }
                else if (
                    acceptHeader.Contains("text/plain")
                    && !acceptHeader.Contains("application/json")
                )
                {
                    // text/plain returns TSV for Nightscout compatibility
                    effectiveFormat = "tsv";
                }
            }

            // Handle different output formats
            if (
                !string.IsNullOrEmpty(effectiveFormat)
                && !effectiveFormat.Equals("json", StringComparison.OrdinalIgnoreCase)
            )
            {
                try
                {
                    var formattedData = _dataFormatService.FormatEntries(
                        entriesArray,
                        effectiveFormat
                    );
                    var contentType = _dataFormatService.GetContentType(effectiveFormat);
                    return Content(formattedData, contentType);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Unsupported format requested: {Format}",
                        effectiveFormat
                    );
                    return BadRequest(
                        new
                        {
                            status = 400,
                            message = $"Unsupported format: {effectiveFormat}. Supported formats: json, csv, tsv, txt",
                            type = "client",
                        }
                    );
                }
            }

            return Ok(entriesArray.ToV1Responses());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving entries");

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
    /// Create new entries
    /// Accepts both single entries and arrays of entries
    /// </summary>
    /// <param name="entryData">Entry data to create (can be single entry or array)</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>Created entries with assigned IDs</returns>
    [HttpPost]
    [NightscoutEndpoint("/api/v1/entries")]
    [ProducesResponseType(typeof(Entry[]), 200)]
    [ProducesResponseType(typeof(object), 400)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<ActionResult<Entry[]>> CreateEntries(
        [FromBody] object entryData,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Create entries endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );
        try
        {
            List<Entry> entriesToCreate = new();

            // Handle different input types
            if (entryData is JsonElement jsonElement)
            {
                // Handle JSON from HTTP requests
                if (jsonElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in jsonElement.EnumerateArray())
                    {
                        var entry = JsonSerializer.Deserialize<Entry>(
                            element.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                        );
                        if (entry != null)
                            entriesToCreate.Add(entry);
                    }
                }
                else
                {
                    var entry = JsonSerializer.Deserialize<Entry>(
                        jsonElement.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (entry != null)
                        entriesToCreate.Add(entry);
                }
            }
            else if (entryData is Entry singleEntry)
            {
                // Handle direct Entry object (for unit tests)
                entriesToCreate.Add(singleEntry);
            }
            else if (entryData is Entry[] entryArray)
            {
                // Handle array of Entry objects (for unit tests)
                entriesToCreate.AddRange(entryArray);
            }
            else if (entryData is IEnumerable<Entry> entryCollection)
            {
                // Handle IEnumerable<Entry> (for unit tests)
                entriesToCreate.AddRange(entryCollection);
            }
            else
            {
                // Try to deserialize as JSON string if it's a raw object
                try
                {
                    var jsonString = JsonSerializer.Serialize(entryData);
                    var element = JsonSerializer.Deserialize<JsonElement>(jsonString);

                    if (element.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var arrayElement in element.EnumerateArray())
                        {
                            var entry = JsonSerializer.Deserialize<Entry>(
                                arrayElement.GetRawText(),
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                            );
                            if (entry != null)
                                entriesToCreate.Add(entry);
                        }
                    }
                    else
                    {
                        var entry = JsonSerializer.Deserialize<Entry>(
                            element.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                        );
                        if (entry != null)
                            entriesToCreate.Add(entry);
                    }
                }
                catch
                {
                    return BadRequest(
                        new
                        {
                            status = 400,
                            message = "Invalid entry data format",
                            type = "client",
                        }
                    );
                }
            }
            if (entriesToCreate.Count == 0)
            {
                return BadRequest(
                    new
                    {
                        status = 400,
                        message = "No valid entries provided",
                        type = "client",
                    }
                );
            } // Validate entries have meaningful data
            var validEntries = new List<Entry>();
            foreach (var entry in entriesToCreate)
            {
                // Check if entry has meaningful glucose data or is a valid non-sgv type
                bool hasMeaningfulData = false;

                // Check for meaningful glucose values
                if (entry.Sgv.HasValue && entry.Sgv.Value > 0)
                    hasMeaningfulData = true;
                if (entry.Mgdl > 0)
                    hasMeaningfulData = true;

                // Check for meaningful timestamp
                if (entry.Mills > 0)
                    hasMeaningfulData = true;
                if (entry.Date.HasValue)
                    hasMeaningfulData = true;
                if (
                    !string.IsNullOrEmpty(entry.DateString)
                    && entry.DateString != "1970-01-01T00:00:00.000Z"
                )
                    hasMeaningfulData = true;

                // Allow non-sgv types with just type specified (like calibrations)
                if (!string.IsNullOrEmpty(entry.Type) && entry.Type != "sgv")
                    hasMeaningfulData = true;

                if (hasMeaningfulData)
                {
                    validEntries.Add(entry);
                }
            }

            if (validEntries.Count == 0)
            {
                return BadRequest(
                    new
                    {
                        status = 400,
                        message = "No valid entries with meaningful data provided",
                        type = "client",
                    }
                );
            }

            // Validate and prepare entries
            foreach (var entry in validEntries)
            {
                // Generate ID if not provided
                if (string.IsNullOrEmpty(entry.Id))
                {
                    entry.Id = Guid.CreateVersion7().ToString("N");
                }

                // Set mills from date if not provided
                if (entry.Mills == 0 && entry.Date.HasValue)
                {
                    var dateValue = entry.Date.Value;
                    var dateOffset =
                        dateValue.Kind == DateTimeKind.Unspecified
                            ? new DateTimeOffset(dateValue, TimeSpan.Zero)
                            : new DateTimeOffset(dateValue);
                    entry.Mills = dateOffset.ToUnixTimeMilliseconds();
                }

                // Set dateString if not provided
                if (string.IsNullOrEmpty(entry.DateString) && entry.Mills > 0)
                {
                    entry.DateString = DateTimeOffset
                        .FromUnixTimeMilliseconds(entry.Mills)
                        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                }

                // Default type to "sgv" if not specified
                if (string.IsNullOrEmpty(entry.Type))
                {
                    entry.Type = "sgv";
                }
            }

            // Process entries for sanitization and timestamp conversion
            var processedEntries = _documentProcessingService.ProcessDocuments(validEntries);
            var processedArray = processedEntries.ToArray();

            // Filter out duplicates using database-backed detection
            var uniqueEntries = new List<Entry>();
            foreach (var entry in processedArray)
            {
                var duplicate = await _entryService.CheckForDuplicateEntryAsync(
                    entry.Device,
                    entry.Type ?? "sgv",
                    entry.Sgv,
                    entry.Mills,
                    windowMinutes: 5,
                    cancellationToken
                );

                if (duplicate != null)
                {
                    _logger.LogDebug(
                        "Skipping duplicate entry: device={Device}, type={Type}, sgv={Sgv}, mills={Mills}",
                        entry.Device,
                        entry.Type,
                        entry.Sgv,
                        entry.Mills
                    );
                    continue;
                }

                uniqueEntries.Add(entry);
            }

            _logger.LogDebug(
                "Filtered {Original} entries to {Unique} unique entries",
                processedArray.Length,
                uniqueEntries.Count
            );

            // Create entries in database
            var createdEntries = await _entryService.CreateEntriesAsync(
                uniqueEntries,
                cancellationToken
            );
            var createdArray = createdEntries.ToArray();

            _logger.LogDebug("Created {Count} entries", createdArray.Length);

            try
            {
                var userId = GetUserId();
                await _alertOrchestrator.EvaluateAndProcessEntriesAsync(
                    createdArray,
                    userId,
                    cancellationToken
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to process alerts for created entries");
            }

            return StatusCode(201, createdArray.ToV1Responses());
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON format in create entries request");
            return BadRequest(
                new
                {
                    status = 400,
                    message = "Invalid JSON format",
                    type = "client",
                    error = ex.Message,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating entries");
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
    /// Update an existing entry by ID
    /// </summary>
    /// <param name="id">The entry ID to update</param>
    /// <param name="entryData">Updated entry data</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>Updated entry</returns>
    [HttpPut("{id}")]
    [NightscoutEndpoint("/api/v1/entries/{id}")]
    [ProducesResponseType(typeof(Entry), 200)]
    [ProducesResponseType(typeof(object), 400)]
    [ProducesResponseType(typeof(object), 404)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<ActionResult<Entry>> UpdateEntry(
        string id,
        [FromBody] Entry entryData,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Update entry endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Validate ID format
            if (
                string.IsNullOrEmpty(id)
                || !System.Text.RegularExpressions.Regex.IsMatch(
                    id,
                    "^[a-f\\d]{24}$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                )
            )
            {
                return BadRequest(
                    new
                    {
                        status = 400,
                        message = "Invalid entry ID format",
                        type = "client",
                    }
                );
            }

            // Ensure the ID in the data matches the URL parameter
            entryData.Id = id;

            var updatedEntry = await _entryService.UpdateEntryAsync(
                id,
                entryData,
                cancellationToken
            );

            if (updatedEntry == null)
            {
                _logger.LogDebug("Entry with ID {Id} not found for update", id);
                return NotFound(
                    new
                    {
                        status = 404,
                        message = "Entry not found",
                        type = "client",
                    }
                );
            }

            _logger.LogDebug("Successfully updated entry with ID: {Id}", id);
            return Ok(updatedEntry.ToV1Response());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating entry with ID: {Id}", id);
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
    /// Delete an entry by ID
    /// </summary>
    /// <param name="id">The entry ID to delete</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>Confirmation of deletion</returns>
    [HttpDelete("{id}")]
    [NightscoutEndpoint("/api/v1/entries/{id}")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(typeof(object), 400)]
    [ProducesResponseType(typeof(object), 404)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<ActionResult> DeleteEntry(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Delete entry endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Validate ID format
            if (
                string.IsNullOrEmpty(id)
                || !System.Text.RegularExpressions.Regex.IsMatch(
                    id,
                    "^[a-f\\d]{24}$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                )
            )
            {
                return BadRequest(
                    new
                    {
                        status = 400,
                        message = "Invalid entry ID format",
                        type = "client",
                    }
                );
            }

            var deleted = await _entryService.DeleteEntryAsync(id, cancellationToken);

            if (!deleted)
            {
                _logger.LogDebug("Entry with ID {Id} not found for deletion", id);
                return NotFound(
                    new
                    {
                        status = 404,
                        message = "Entry not found",
                        type = "client",
                    }
                );
            }

            _logger.LogDebug("Successfully deleted entry with ID: {Id}", id);
            return Ok(
                new
                {
                    status = 200,
                    message = "Entry deleted successfully",
                    type = "success",
                    id = id,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting entry with ID: {Id}", id);
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
    /// Bulk delete entries with query filter
    /// </summary>
    /// <param name="find">MongoDB-style find query filters (JSON format)</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>Confirmation of bulk deletion</returns>
    [HttpDelete]
    [NightscoutEndpoint("/api/v1/entries")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(typeof(object), 400)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<ActionResult> BulkDeleteEntries(
        [FromQuery] string? find = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Bulk delete entries endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            string? findQuery = find;

            // If no simple find parameter provided, check for complex query parameters
            if (string.IsNullOrEmpty(findQuery))
            {
                var queryString = HttpContext?.Request?.QueryString.ToString() ?? "";

                if (!string.IsNullOrEmpty(queryString) && queryString != "?")
                {
                    // Remove the leading '?' from query string and use it as the find query
                    findQuery = queryString.TrimStart('?');
                }
            }

            if (string.IsNullOrEmpty(findQuery))
            {
                return BadRequest(
                    new
                    {
                        status = 400,
                        message = "Find query parameter is required for bulk delete",
                        type = "client",
                    }
                );
            }

            var deletedCount = await _entryService.DeleteEntriesAsync(findQuery, cancellationToken);

            _logger.LogDebug("Successfully deleted {Count} entries with query", deletedCount);
            return Ok(
                new
                {
                    status = 200,
                    message = $"Deleted {deletedCount} entries",
                    type = "success",
                    deletedCount = deletedCount,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in bulk delete entries");
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
    /// Create new entries asynchronously
    /// Accepts both single entries and arrays of entries, returns immediately with tracking information
    /// </summary>
    /// <param name="entryData">Entry data to create (can be single entry or array)</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>Async processing response with correlation ID and status URL</returns>
    [HttpPost("async")]
    [NightscoutEndpoint("/api/v1/entries/async")]
    [ProducesResponseType(typeof(AsyncProcessingResponse), 202)]
    [ProducesResponseType(typeof(object), 400)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<ActionResult<AsyncProcessingResponse>> CreateEntriesAsync(
        [FromBody] object entryData,
        CancellationToken cancellationToken = default
    )
    {
        var correlationId = Guid.CreateVersion7().ToString();

        _logger.LogDebug(
            "Async create entries endpoint requested with correlation ID: {CorrelationId} from {RemoteIpAddress}",
            correlationId,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            List<Entry> entriesToCreate = new();

            // Handle different input types (same logic as sync endpoint)
            if (entryData is JsonElement jsonElement)
            {
                // Handle JSON from HTTP requests
                if (jsonElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in jsonElement.EnumerateArray())
                    {
                        var entry = JsonSerializer.Deserialize<Entry>(
                            element.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                        );
                        if (entry != null)
                            entriesToCreate.Add(entry);
                    }
                }
                else
                {
                    var entry = JsonSerializer.Deserialize<Entry>(
                        jsonElement.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (entry != null)
                        entriesToCreate.Add(entry);
                }
            }
            else if (entryData is Entry singleEntry)
            {
                // Handle direct Entry object
                entriesToCreate.Add(singleEntry);
            }
            else if (entryData is Entry[] entryArray)
            {
                // Handle array of Entry objects
                entriesToCreate.AddRange(entryArray);
            }
            else if (entryData is IEnumerable<Entry> entryCollection)
            {
                // Handle IEnumerable<Entry>
                entriesToCreate.AddRange(entryCollection);
            }
            else
            {
                // Try to deserialize as JSON string if it's a raw object
                try
                {
                    var jsonString = JsonSerializer.Serialize(entryData);
                    var element = JsonSerializer.Deserialize<JsonElement>(jsonString);

                    if (element.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var arrayElement in element.EnumerateArray())
                        {
                            var entry = JsonSerializer.Deserialize<Entry>(
                                arrayElement.GetRawText(),
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                            );
                            if (entry != null)
                                entriesToCreate.Add(entry);
                        }
                    }
                    else
                    {
                        var entry = JsonSerializer.Deserialize<Entry>(
                            element.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                        );
                        if (entry != null)
                            entriesToCreate.Add(entry);
                    }
                }
                catch
                {
                    return BadRequest(
                        new
                        {
                            status = 400,
                            message = "Invalid entry data format",
                            type = "client",
                            correlationId = correlationId,
                        }
                    );
                }
            }

            if (entriesToCreate.Count == 0)
            {
                return BadRequest(
                    new
                    {
                        status = 400,
                        message = "No valid entries provided",
                        type = "client",
                        correlationId = correlationId,
                    }
                );
            }

            // Basic validation (same as sync endpoint)
            var validEntries = new List<Entry>();
            foreach (var entry in entriesToCreate)
            {
                // Check if entry has meaningful data or is a valid non-sgv type
                bool hasMeaningfulData = false;

                // Check for meaningful glucose values
                if (entry.Sgv.HasValue && entry.Sgv.Value > 0)
                    hasMeaningfulData = true;
                if (entry.Mgdl > 0)
                    hasMeaningfulData = true;

                // Check for meaningful timestamp
                if (entry.Mills > 0)
                    hasMeaningfulData = true;
                if (entry.Date.HasValue)
                    hasMeaningfulData = true;
                if (
                    !string.IsNullOrEmpty(entry.DateString)
                    && entry.DateString != "1970-01-01T00:00:00.000Z"
                )
                    hasMeaningfulData = true;

                // Allow non-sgv types with just type specified (like calibrations)
                if (!string.IsNullOrEmpty(entry.Type) && entry.Type != "sgv")
                    hasMeaningfulData = true;

                if (hasMeaningfulData)
                {
                    validEntries.Add(entry);
                }
            }

            if (validEntries.Count == 0)
            {
                return BadRequest(
                    new
                    {
                        status = 400,
                        message = "No valid entries with meaningful data provided",
                        type = "client",
                        correlationId = correlationId,
                    }
                );
            }

            // Initialize processing status
            await _processingStatusService.InitializeAsync(
                correlationId,
                validEntries.Count,
                cancellationToken
            );

            // Prepare entries for processing (same validation as sync endpoint)
            foreach (var entry in validEntries)
            {
                // Generate ID if not provided
                if (string.IsNullOrEmpty(entry.Id))
                {
                    entry.Id = Guid.CreateVersion7().ToString("N");
                }

                // Set mills from date if not provided
                if (entry.Mills == 0 && entry.Date.HasValue)
                {
                    entry.Mills = ((DateTimeOffset)entry.Date.Value).ToUnixTimeMilliseconds();
                }

                // Set dateString if not provided
                if (string.IsNullOrEmpty(entry.DateString) && entry.Mills > 0)
                {
                    entry.DateString = DateTimeOffset
                        .FromUnixTimeMilliseconds(entry.Mills)
                        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                }

                // Default type to "sgv" if not specified
                if (string.IsNullOrEmpty(entry.Type))
                {
                    entry.Type = "sgv";
                }
            }

            // Process entries for sanitization and timestamp conversion
            var processedEntries = _documentProcessingService.ProcessDocuments(validEntries);
            var processedArray = processedEntries.ToArray();

            // Filter out duplicates using database-backed detection
            var uniqueEntries = new List<Entry>();
            foreach (var entry in processedArray)
            {
                var duplicate = await _entryService.CheckForDuplicateEntryAsync(
                    entry.Device,
                    entry.Type ?? "sgv",
                    entry.Sgv,
                    entry.Mills,
                    windowMinutes: 5,
                    cancellationToken
                );

                if (duplicate != null)
                {
                    _logger.LogDebug(
                        "Skipping duplicate entry: device={Device}, type={Type}, sgv={Sgv}, mills={Mills}",
                        entry.Device,
                        entry.Type,
                        entry.Sgv,
                        entry.Mills
                    );
                    continue;
                }

                uniqueEntries.Add(entry);
            }

            _logger.LogDebug(
                "Filtered {Original} entries to {Unique} unique entries",
                processedArray.Length,
                uniqueEntries.Count
            );

            // Create entries in database synchronously
            var createdEntries = await _entryService.CreateEntriesAsync(
                uniqueEntries,
                cancellationToken
            );

            // Mark processing as completed
            await _processingStatusService.MarkCompletedAsync(
                correlationId,
                createdEntries.Count(),
                cancellationToken
            );

            var response = new AsyncProcessingResponse
            {
                CorrelationId = correlationId,
                Status = "completed",
                StatusUrl = $"/api/v1/processing/status/{correlationId}",
                EstimatedProcessingTime = TimeSpan.Zero,
            };

            _logger.LogInformation(
                "Completed async entries request with correlation ID: {CorrelationId} for {EntryCount} entries",
                correlationId,
                createdEntries.Count()
            );

            try
            {
                var userId = GetUserId();
                await _alertOrchestrator.EvaluateAndProcessEntriesAsync(
                    createdEntries,
                    userId,
                    cancellationToken
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to process alerts for async entries");
            }

            return Accepted(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing async entries request with correlation ID: {CorrelationId}",
                correlationId
            );

            // Try to mark as failed if status was initialized
            try
            {
                await _processingStatusService.MarkFailedAsync(
                    correlationId,
                    new[] { ex.Message },
                    cancellationToken
                );
            }
            catch (Exception statusUpdateEx)
            {
                _logger.LogDebug(statusUpdateEx, "Failed to update processing status to failed");
            }

            return StatusCode(
                500,
                new
                {
                    status = 500,
                    message = "Internal server error",
                    type = "internal",
                    error = ex.Message,
                    correlationId = correlationId,
                }
            );
        }
    }

    private string GetUserId()
    {
        return HttpContext.GetSubjectIdString()
            ?? "00000000-0000-0000-0000-000000000001";
    }
}
