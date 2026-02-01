using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Extensions;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// V3 Entries controller that provides full V3 API compatibility with Nightscout entries endpoints
/// Implements the /api/v3/entries endpoints with pagination, field selection, sorting, and advanced filtering
/// </summary>
[ApiController]
[Route("api/v3/[controller]")]
public class EntriesController : BaseV3Controller<Entry>
{
    private readonly IEntryService _entryService;
    private readonly IAlertOrchestrator _alertOrchestrator;

    public EntriesController(
        IPostgreSqlService postgreSqlService,
        IDataFormatService dataFormatService,
        IDocumentProcessingService documentProcessingService,
        IEntryService entryService,
        IAlertOrchestrator alertOrchestrator,
        ILogger<EntriesController> logger
    )
        : base(postgreSqlService, dataFormatService, documentProcessingService, logger)
    {
        _entryService = entryService;
        _alertOrchestrator = alertOrchestrator;
    }

    /// <summary>
    /// Get entries with V3 API features including pagination, field selection, and advanced filtering
    /// </summary>
    /// <returns>V3 entries collection response</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v3/entries")]
    [ProducesResponseType(typeof(V3CollectionResponse<object>), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(304)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetEntries(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "V3 entries endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var parameters = ParseV3QueryParameters();

            // Convert V3 parameters to V1 query for backend compatibility
            var type = ExtractTypeFromFilter(parameters.Filter);

            // Convert V3 filter criteria (field$op=value) to MongoDB-style JSON query
            var findQuery =
                ConvertFilterCriteriaToFindQuery(parameters.FilterCriteria)
                ?? ConvertV3FilterToV1Find(parameters.Filter);

            // Determine sort direction from sort$desc query parameter
            // Nightscout V3: sort$desc=field means descending (newest first)
            // reverseResults=false means descending, reverseResults=true means ascending
            var hasSortDesc = HttpContext?.Request.Query.ContainsKey("sort$desc") ?? false;
            var reverseResults = !hasSortDesc && ExtractSortDirection(parameters.Sort);

            // Get entries using existing V1 backend with V3 parameters
            var entries = await _postgreSqlService.GetEntriesWithAdvancedFilterAsync(
                type: type,
                count: parameters.Limit,
                skip: parameters.Offset,
                findQuery: findQuery,
                dateString: null, // V3 uses filter instead
                reverseResults: reverseResults,
                cancellationToken: cancellationToken
            );

            var entriesList = entries.ToList();

            // Check for conditional requests (304 Not Modified)
            var lastModified = GetLastModified(entriesList);
            var etag = GenerateETag(entriesList);

            if (ShouldReturn304(etag, lastModified, parameters))
            {
                return StatusCode(304);
            }

            // Transform entries to V3 response format with computed fields
            var v3Entries = entriesList.ToV3Responses().ToList();

            _logger.LogDebug(
                "Successfully returned {Count} entries with V3 format",
                entriesList.Count
            );

            // Return Nightscout V3-compatible response with transformed V3 entries
            return CreateV3SuccessResponse(v3Entries);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 entries request parameters");
            return CreateV3ErrorResponse(400, "Invalid request parameters", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving V3 entries");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Get a specific entry by ID with V3 format
    /// </summary>
    /// <param name="id">Entry ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Single entry in V3 format</returns>
    [HttpGet("{id}")]
    [NightscoutEndpoint("/api/v3/entries/:id")]
    [ProducesResponseType(typeof(Entry), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Entry>> GetEntry(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 entry by ID requested: {Id}", id);

        try
        {
            var entry = await _postgreSqlService.GetEntryByIdAsync(id, cancellationToken);

            if (entry == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Entry not found",
                    $"No entry found with ID: {id}"
                );
            }

            // Set appropriate headers
            var etag = GenerateETag(new[] { entry });
            Response.Headers["ETag"] = $"\"{etag}\"";
            Response.Headers["Cache-Control"] = "public, max-age=60";

            return Ok(entry.ToV3Response());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving V3 entry {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Create a new entry via V3 API
    /// </summary>
    /// <param name="entry">Entry to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created entry</returns>
    [HttpPost]
    [NightscoutEndpoint("/api/v3/entries")]
    [ProducesResponseType(typeof(Entry), 201)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Entry>> CreateEntry(
        [FromBody] Entry entry,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 entry creation requested");

        try
        {
            if (entry == null)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Entry data is required",
                    "Request body cannot be null"
                );
            }

            // Process the entry
            var processedEntry = _documentProcessingService.ProcessEntry(entry); // Save to database
            var createdEntries = await _entryService.CreateEntriesAsync(
                new[] { processedEntry },
                cancellationToken
            );
            var createdEntry = createdEntries.FirstOrDefault();

            if (createdEntry == null)
            {
                return CreateV3ErrorResponse(
                    500,
                    "Failed to create entry",
                    "Entry creation failed"
                );
            }

            _logger.LogDebug("Successfully created V3 entry {Id}", createdEntry.Id);

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
                _logger.LogWarning(ex, "Failed to process alerts for V3 entry creation");
            }

            // Set location header for created resource
            Response.Headers["Location"] = $"/api/v3/entries/{createdEntry.Id}";

            return CreatedAtAction(
                nameof(GetEntry),
                new { id = createdEntry.Id },
                createdEntry.ToV3Response()
            );
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 entry data");
            return CreateV3ErrorResponse(400, "Invalid entry data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating V3 entry");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Create multiple entries via V3 API (bulk operation)
    /// </summary>
    /// <param name="entries">Entries to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created entries</returns>
    [HttpPost("bulk")]
    [NightscoutEndpoint("/api/v3/entries/bulk")]
    [ProducesResponseType(typeof(Entry[]), 201)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Entry[]>> CreateEntries(
        [FromBody] Entry[] entries,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 bulk entry creation requested for {Count} entries",
            entries?.Length ?? 0
        );

        try
        {
            if (entries == null || entries.Length == 0)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Entries data is required",
                    "Request body must contain at least one entry"
                );
            }

            // Validate bulk limit
            if (entries.Length > 1000)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Too many entries",
                    "Bulk operations are limited to 1000 entries per request"
                );
            }

            // Process all entries
            var processedEntries = entries
                .Select(entry => _documentProcessingService.ProcessEntry(entry))
                .ToList();

            // Save to database
            var createdEntries = await _entryService.CreateEntriesAsync(
                processedEntries,
                cancellationToken
            );

            _logger.LogDebug(
                "Successfully created {Count} V3 entries via bulk operation",
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
                _logger.LogWarning(ex, "Failed to process alerts for V3 bulk entries");
            }

            return StatusCode(201, createdEntries.ToV3Responses());
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 bulk entry data");
            return CreateV3ErrorResponse(400, "Invalid entries data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating V3 bulk entries");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Update an entry via V3 API
    /// </summary>
    /// <param name="id">Entry ID</param>
    /// <param name="entry">Updated entry data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated entry</returns>
    [HttpPut("{id}")]
    [NightscoutEndpoint("/api/v3/entries/:id")]
    [ProducesResponseType(typeof(Entry), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Entry>> UpdateEntry(
        string id,
        [FromBody] Entry entry,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 entry update requested for {Id}", id);

        try
        {
            if (entry == null)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Entry data is required",
                    "Request body cannot be null"
                );
            }

            // Ensure the ID matches
            entry.Id = id;

            // Process the entry
            var processedEntry = _documentProcessingService.ProcessEntry(entry);

            // Update in database
            var updatedEntry = await _postgreSqlService.UpdateEntryAsync(
                id,
                processedEntry,
                cancellationToken
            );

            if (updatedEntry == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Entry not found",
                    $"No entry found with ID: {id}"
                );
            }

            _logger.LogDebug("Successfully updated V3 entry {Id}", id);

            return Ok(updatedEntry.ToV3Response());
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 entry update data for {Id}", id);
            return CreateV3ErrorResponse(400, "Invalid entry data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating V3 entry {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Delete an entry via V3 API
    /// </summary>
    /// <param name="id">Entry ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    [HttpDelete("{id}")]
    [NightscoutEndpoint("/api/v3/entries/:id")]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteEntry(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 entry deletion requested for {Id}", id);

        try
        {
            var deleted = await _postgreSqlService.DeleteEntryAsync(id, cancellationToken);

            if (!deleted)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Entry not found",
                    $"No entry found with ID: {id}"
                );
            }

            _logger.LogDebug("Successfully deleted V3 entry {Id}", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting V3 entry {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    #region Helper Methods

    private new string? ExtractTypeFromFilter(JsonElement? filter)
    {
        if (!filter.HasValue)
            return null;

        try
        {
            if (filter.Value.TryGetProperty("type", out var typeElement))
            {
                return typeElement.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract type from V3 filter");
        }

        return null;
    }

    private new string? ConvertV3FilterToV1Find(JsonElement? filter)
    {
        if (!filter.HasValue)
            return null;

        try
        {
            // Convert V3 JSON filter to V1 query string format
            // This is a simplified conversion - full implementation would need more sophisticated mapping
            var filterObj = filter.Value;
            var queryParts = new List<string>();

            foreach (var property in filterObj.EnumerateObject())
            {
                if (property.Name == "type")
                    continue; // Handled separately

                var value = property.Value;
                if (value.ValueKind == JsonValueKind.Object)
                {
                    // Handle operators like $gte, $lte, etc.
                    foreach (var op in value.EnumerateObject())
                    {
                        queryParts.Add($"find[{property.Name}][{op.Name}]={op.Value}");
                    }
                }
                else
                {
                    // Simple equality
                    queryParts.Add($"find[{property.Name}]={value}");
                }
            }

            return queryParts.Count > 0 ? string.Join("&", queryParts) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert V3 filter to V1 find query");
            return null;
        }
    }

    /// <summary>
    /// Convert V3 filter criteria (field$op=value format) to MongoDB-style JSON query
    /// </summary>
    /// <param name="filterCriteria">List of parsed filter criteria</param>
    /// <returns>MongoDB-style JSON query string, or null if no criteria</returns>
    private string? ConvertFilterCriteriaToFindQuery(List<V3FilterCriteria>? filterCriteria)
    {
        if (filterCriteria == null || filterCriteria.Count == 0)
            return null;

        var conditions = new Dictionary<string, object>();

        foreach (var criteria in filterCriteria)
        {
            var mongoOp = criteria.Operator switch
            {
                "eq" => null, // Direct equality doesn't need operator
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

            if (mongoOp == null && criteria.Operator == "eq")
            {
                // Direct equality: { "field": "value" }
                conditions[criteria.Field] = criteria.Value ?? "";
            }
            else if (mongoOp != null)
            {
                // Operator form: { "field": { "$op": "value" } }
                conditions[criteria.Field] = new Dictionary<string, object?>
                {
                    [mongoOp] = criteria.Value,
                };
            }
        }

        if (conditions.Count == 0)
            return null;

        return JsonSerializer.Serialize(conditions);
    }

    private async Task<long> GetTotalCountAsync(
        string? type,
        string? findQuery,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Use the count endpoint to get total
            return await _postgreSqlService.CountEntriesAsync(type, findQuery, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get total count for V3 entries, using estimate");
            // Return a reasonable estimate if count fails
            return 1000;
        }
    }

    private DateTimeOffset GetLastModified(List<Entry> entries)
    {
        if (entries.Count == 0)
            return DateTimeOffset.UtcNow;

        // Use the most recent entry's date as last modified
        var latestMills = entries.Max(e => e.Mills);
        return DateTimeOffset.FromUnixTimeMilliseconds(latestMills);
    }

    private string GetUserId()
    {
        return HttpContext.GetSubjectIdString()
            ?? "00000000-0000-0000-0000-000000000001";
    }

    #endregion
}
