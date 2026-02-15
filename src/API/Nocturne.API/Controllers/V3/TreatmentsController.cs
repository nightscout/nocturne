using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// V3 Treatments controller that provides full V3 API compatibility with Nightscout treatments endpoints
/// Implements the /api/v3/treatments endpoints with pagination, field selection, sorting, and advanced filtering
/// </summary>
[ApiController]
[Route("api/v3/[controller]")]
public class TreatmentsController : BaseV3Controller<Treatment>
{
    private readonly ITreatmentService _treatmentService;

    public TreatmentsController(
        IPostgreSqlService postgreSqlService,
        IDataFormatService dataFormatService,
        IDocumentProcessingService documentProcessingService,
        ITreatmentService treatmentService,
        ILogger<TreatmentsController> logger
    )
        : base(postgreSqlService, dataFormatService, documentProcessingService, logger)
    {
        _treatmentService = treatmentService;
    }

    /// <summary>
    /// Get treatments with V3 API features including pagination, field selection, and advanced filtering
    /// </summary>
    /// <returns>V3 treatments collection response</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v3/treatments")]
    [ProducesResponseType(typeof(V3CollectionResponse<object>), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(304)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetTreatments(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "V3 treatments endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var parameters = ParseV3QueryParameters();

            // Convert V3 filter criteria (field$op=value) to MongoDB-style JSON query
            var findQuery =
                ConvertFilterCriteriaToFindQuery(parameters.FilterCriteria)
                ?? ConvertV3FilterToV1Find(parameters.Filter);

            // Determine sort direction from sort$desc query parameter
            // Nightscout V3: sort$desc=field means descending (newest first)
            // reverseResults=false means descending, reverseResults=true means ascending
            var hasSortDesc = HttpContext?.Request.Query.ContainsKey("sort$desc") ?? false;
            var reverseResults = !hasSortDesc && ExtractSortDirection(parameters.Sort);

            // Get treatments using existing backend with V3 parameters
            var treatments = await _treatmentService.GetTreatmentsWithAdvancedFilterAsync(
                count: parameters.Limit,
                skip: parameters.Offset,
                findQuery: findQuery,
                reverseResults: reverseResults,
                cancellationToken: cancellationToken
            );

            var treatmentsList = treatments.ToList();

            // Get total count for pagination
            var totalCount = await GetTotalCountAsync(findQuery, cancellationToken);

            // Check for conditional requests (304 Not Modified)
            var lastModified = GetLastModified(treatmentsList);
            var etag = GenerateETag(treatmentsList);

            if (ShouldReturn304(etag, lastModified, parameters))
            {
                return StatusCode(304);
            }

            _logger.LogDebug(
                "Successfully returned {Count} treatments with V3 format",
                treatmentsList.Count
            );

            // Return Nightscout V3-compatible response: {"status": 200, "result": [...]}
            return CreateV3SuccessResponse(treatmentsList);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 treatments request parameters");
            return CreateV3ErrorResponse(400, "Invalid request parameters", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving V3 treatments");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Get a specific treatment by ID with V3 format
    /// </summary>
    /// <param name="id">Treatment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Single treatment in V3 format</returns>
    [HttpGet("{id}")]
    [NightscoutEndpoint("/api/v3/treatments/:id")]
    [ProducesResponseType(typeof(Treatment), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment>> GetTreatment(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 treatment by ID requested: {Id}", id);

        try
        {
            var treatment = await _treatmentService.GetTreatmentByIdAsync(id, cancellationToken);

            if (treatment == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Treatment not found",
                    $"No treatment found with ID: {id}"
                );
            }

            // Set appropriate headers
            var etag = GenerateETag(new[] { treatment });
            Response.Headers["ETag"] = $"\"{etag}\"";
            Response.Headers["Cache-Control"] = "public, max-age=60";

            return Ok(treatment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving V3 treatment {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Create a new treatment via V3 API
    /// </summary>
    /// <param name="treatment">Treatment to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created treatment</returns>
    [HttpPost]
    [NightscoutEndpoint("/api/v3/treatments")]
    [ProducesResponseType(typeof(Treatment), 201)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment>> CreateTreatment(
        [FromBody] Treatment treatment,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 treatment creation requested");

        try
        {
            if (treatment == null)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Treatment data is required",
                    "Request body cannot be null"
                );
            }

            // Check for duplicate treatment (AAPS expects isDeduplication response)
            // Route through TreatmentService to check both treatments table and StateSpans
            if (!string.IsNullOrEmpty(treatment.Id))
            {
                var existingTreatment = await _treatmentService.GetTreatmentByIdAsync(
                    treatment.Id,
                    cancellationToken
                );
                if (existingTreatment != null)
                {
                    return Ok(
                        new
                        {
                            status = 200,
                            identifier = existingTreatment.Id,
                            isDeduplication = true,
                            deduplicatedIdentifier = existingTreatment.Id,
                        }
                    );
                }
            }

            // Process the treatment
            var processedTreatment = _documentProcessingService.ProcessTreatment(treatment);

            // Route through TreatmentService which handles StateSpan creation for temp basals
            var created = await _treatmentService.CreateTreatmentsAsync(
                [processedTreatment],
                cancellationToken
            );

            var createdTreatment = created.FirstOrDefault();
            if (createdTreatment == null)
            {
                return CreateV3ErrorResponse(
                    500,
                    "Failed to create treatment",
                    "Treatment creation failed"
                );
            }

            _logger.LogDebug("Successfully created V3 treatment {Id}", createdTreatment.Id);

            // Set location header for created resource
            Response.Headers["Location"] = $"/api/v3/treatments/{createdTreatment.Id}";

            return CreatedAtAction(
                nameof(GetTreatment),
                new { id = createdTreatment.Id },
                createdTreatment
            );
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 treatment data");
            return CreateV3ErrorResponse(400, "Invalid treatment data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating V3 treatment");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Create multiple treatments via V3 API (bulk operation)
    /// </summary>
    /// <param name="treatments">Treatments to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created treatments</returns>
    [HttpPost("bulk")]
    [NightscoutEndpoint("/api/v3/treatments/bulk")]
    [ProducesResponseType(typeof(Treatment[]), 201)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment[]>> CreateTreatments(
        [FromBody] Treatment[] treatments,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 bulk treatment creation requested for {Count} treatments",
            treatments?.Length ?? 0
        );

        try
        {
            if (treatments == null || treatments.Length == 0)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Treatments data is required",
                    "Request body must contain at least one treatment"
                );
            }

            // Validate bulk limit
            if (treatments.Length > 1000)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Too many treatments",
                    "Bulk operations are limited to 1000 treatments per request"
                );
            }

            // Process all treatments
            var processedTreatments = treatments
                .Select(treatment => _documentProcessingService.ProcessTreatment(treatment))
                .ToList();

            // Save to database
            var createdTreatments = await _treatmentService.CreateTreatmentsAsync(
                processedTreatments,
                cancellationToken
            );

            var createdArray = createdTreatments.ToArray();
            _logger.LogDebug(
                "Successfully created {Count} V3 treatments via bulk operation",
                createdArray.Length
            );

            return StatusCode(201, createdArray);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 bulk treatment data");
            return CreateV3ErrorResponse(400, "Invalid treatments data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating V3 bulk treatments");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Update a treatment via V3 API
    /// </summary>
    /// <param name="id">Treatment ID</param>
    /// <param name="treatment">Updated treatment data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated treatment</returns>
    [HttpPut("{id}")]
    [NightscoutEndpoint("/api/v3/treatments/:id")]
    [ProducesResponseType(typeof(Treatment), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment>> UpdateTreatment(
        string id,
        [FromBody] Treatment treatment,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 treatment update requested for {Id}", id);

        try
        {
            if (treatment == null)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Treatment data is required",
                    "Request body cannot be null"
                );
            }

            // Ensure the ID matches
            treatment.Id = id;

            // Process the treatment
            var processedTreatment = _documentProcessingService.ProcessTreatment(treatment);

            // Update in database
            var updatedTreatment = await _treatmentService.UpdateTreatmentAsync(
                id,
                processedTreatment,
                cancellationToken
            );

            if (updatedTreatment == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Treatment not found",
                    $"No treatment found with ID: {id}"
                );
            }

            _logger.LogDebug("Successfully updated V3 treatment {Id}", id);

            return Ok(updatedTreatment);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 treatment update data for {Id}", id);
            return CreateV3ErrorResponse(400, "Invalid treatment data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating V3 treatment {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Delete a treatment via V3 API
    /// </summary>
    /// <param name="id">Treatment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    [HttpDelete("{id}")]
    [NightscoutEndpoint("/api/v3/treatments/:id")]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteTreatment(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 treatment deletion requested for {Id}", id);

        try
        {
            var deleted = await _treatmentService.DeleteTreatmentAsync(id, cancellationToken);

            if (!deleted)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Treatment not found",
                    $"No treatment found with ID: {id}"
                );
            }

            _logger.LogDebug("Successfully deleted V3 treatment {Id}", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting V3 treatment {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Get treatments modified since a given timestamp (for AAPS incremental sync)
    /// </summary>
    [HttpGet("history/{lastModified:long}")]
    [NightscoutEndpoint("/api/v3/treatments/history/{lastModified}")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetTreatmentHistory(
        long lastModified,
        [FromQuery] int limit = 1000,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 treatment history requested since {LastModified} with limit {Limit}",
            lastModified,
            limit
        );

        try
        {
            limit = Math.Min(Math.Max(limit, 1), 1000);

            var treatments = await _treatmentService.GetTreatmentsModifiedSinceAsync(
                lastModified,
                limit,
                cancellationToken
            );
            return CreateV3SuccessResponse(treatments);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving treatment history");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Partially update a treatment via V3 API (JSON merge-patch)
    /// Used by AAPS to update Temp Basal duration, endId, etc.
    /// </summary>
    [HttpPatch("{id}")]
    [NightscoutEndpoint("/api/v3/treatments/:id")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> PatchTreatment(
        string id,
        [FromBody] JsonElement patchData,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 treatment PATCH requested for {Id}", id);

        try
        {
            var result = await _treatmentService.PatchTreatmentAsync(
                id,
                patchData,
                cancellationToken
            );

            if (result == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Treatment not found",
                    $"No treatment found with ID: {id}"
                );
            }

            _logger.LogDebug("Successfully patched V3 treatment {Id}", id);

            return Ok(
                new
                {
                    status = 200,
                    result,
                    identifier = result.Id,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error patching V3 treatment {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    #region Helper Methods

    private new string? ConvertV3FilterToV1Find(JsonElement? filter)
    {
        if (!filter.HasValue)
            return null;

        try
        {
            // Convert V3 JSON filter to V1 query string format
            var filterObj = filter.Value;
            var queryParts = new List<string>();

            foreach (var property in filterObj.EnumerateObject())
            {
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
        string? findQuery,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Use the count endpoint to get total
            return await _postgreSqlService.CountTreatmentsAsync(findQuery, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get total count for V3 treatments, using estimate");
            // Return a reasonable estimate if count fails
            return 1000;
        }
    }

    private DateTimeOffset GetLastModified(List<Treatment> treatments)
    {
        if (treatments.Count == 0)
            return DateTimeOffset.UtcNow;

        // Use the most recent treatment's created_at as last modified
        var latestCreatedAt = treatments
            .Where(t => !string.IsNullOrEmpty(t.CreatedAt))
            .Select(t => DateTime.Parse(t.CreatedAt!))
            .DefaultIfEmpty(DateTime.UtcNow)
            .Max();

        // Ensure DateTime is treated as UTC to avoid ArgumentException when creating DateTimeOffset
        return new DateTimeOffset(
            DateTime.SpecifyKind(latestCreatedAt, DateTimeKind.Utc),
            TimeSpan.Zero
        );
    }

    #endregion
}
