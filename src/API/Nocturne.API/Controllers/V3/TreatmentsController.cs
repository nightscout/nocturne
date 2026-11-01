using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Helpers;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// V3 Treatments controller that provides full V3 API compatibility with Nightscout treatments endpoints.
/// Implements the /api/v3/treatments endpoints with pagination, field selection, sorting, and advanced filtering.
/// </summary>
/// <seealso cref="ITreatmentService"/>
/// <seealso cref="Treatment"/>
/// <seealso cref="BaseV3Controller{T}"/>
[ApiController]
[Tags("V3")]
[Route("api/v3/[controller]")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class TreatmentsController : BaseV3Controller<Treatment>
{
    private readonly ITreatmentService _treatmentService;

    public TreatmentsController(
        IDocumentProcessingService documentProcessingService,
        ITreatmentService treatmentService,
        ILogger<TreatmentsController> logger
    )
        : base(documentProcessingService, logger)
    {
        _treatmentService = treatmentService;
    }

    /// <summary>
    /// Get treatments with V3 API features including pagination, field selection, and advanced filtering.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A Nightscout V3-compatible response containing <see cref="Treatment"/> objects
    /// wrapped in <c>{"status": 200, "result": [...]}</c>.
    /// </returns>
    /// <remarks>
    /// Supports the full V3 query parameter set including <see cref="V3FilterCriteria"/>-based filtering.
    /// Conditional requests via If-None-Match and If-Modified-Since return 304 when data has not changed.
    /// </remarks>
    /// <response code="200">V3 collection of treatments.</response>
    /// <response code="304">Not modified.</response>
    /// <response code="400">Invalid request parameters.</response>
    /// <response code="500">Internal server error.</response>
    [HttpGet]
    [NightscoutEndpoint("/api/v3/treatments")]
    [ProducesResponseType(typeof(V3CollectionResponse<object>), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(304)]
    [ProducesResponseType(500)]
    [RequireScope(Scope.TreatmentsRead)]
    [ErrorEnvelope]
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

            var treatments = await _treatmentService.QueryTreatmentsAsync(
                new TreatmentQuery
                {
                    Find = findQuery,
                    Count = parameters.Limit,
                    Skip = parameters.Offset,
                    ReverseResults = !parameters.Sort.Descending,
                    SortField = ReadSortFieldOf(parameters.Sort),
                },
                cancellationToken
            );

            var treatmentsList = treatments.ToList();

            // Check for conditional requests (304 Not Modified)
            var lastModified = GetLastModified(treatmentsList);

            if (ShouldReturn304(lastModified, parameters))
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
    [RequireScope(Scope.TreatmentsRead)]
    [ErrorEnvelope]
    public async Task<ActionResult<Treatment>> GetTreatment(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 treatment by ID requested: {Id}", id);

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
        Response.Headers["ETag"] = FormatCursorETag(treatment.SrvModified ?? treatment.Mills);
        Response.Headers["Cache-Control"] = "private, max-age=60";

        return Ok(treatment);
    }

    /// <summary>
    /// Create a new treatment via V3 API.
    /// </summary>
    /// <param name="treatment">The <see cref="Treatment"/> to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <see cref="V3CreateResponse"/> for the created or already stored treatment.</returns>
    /// <remarks>
    /// Supports AAPS deduplication: if a treatment with the same ID already exists,
    /// returns 200 with <c>isDeduplication: true</c>.
    /// Treatments are routed through <see cref="ITreatmentService"/> which handles
    /// StateSpan creation for temp basals and other event-type-specific processing.
    /// </remarks>
    /// <response code="201">Treatment created successfully.</response>
    /// <response code="200">Duplicate treatment detected (deduplication response for AAPS).</response>
    /// <response code="400">Invalid treatment data.</response>
    /// <response code="500">Internal server error.</response>
    [HttpPost]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v3/treatments")]
    [ProducesResponseType(typeof(V3CreateResponse), 201)]
    [ProducesResponseType(typeof(V3CreateResponse), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult> CreateTreatment(
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
                    return V3Deduplicated(existingTreatment.Id, existingTreatment.SrvModified);
                }
            }

            // Process the treatment
            var processedTreatment = _documentProcessingService.ProcessTreatment(treatment);

            // Route through TreatmentService which handles StateSpan creation for temp basals
            var created = await _treatmentService.CreateTreatmentsAsync(
                [processedTreatment],
                cancellationToken: cancellationToken
            );

            if (created.Rejected is [{ Reason: var reason }])
                return CreateV3ErrorResponse(400, "Treatment cannot be stored", reason);

            var createdTreatment = created[0];

            _logger.LogDebug("Successfully created V3 treatment {Id}", createdTreatment.Id);

            return V3Created(nameof(GetTreatment), createdTreatment.Id);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 treatment data");
            return CreateV3ErrorResponse(400, "Invalid treatment data", ex.Message);
        }
    }

    /// <summary>
    /// Create multiple treatments via V3 API (bulk operation)
    /// </summary>
    /// <param name="treatments">Treatments to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created treatments</returns>
    [HttpPost("bulk")]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v3/treatments/bulk")]
    [ProducesResponseType(typeof(Treatment[]), 201)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
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
                cancellationToken: cancellationToken
            );

            var createdArray = TreatmentEcho.InSubmittedOrder(createdTreatments);
            _logger.LogDebug(
                "Successfully created {Count} V3 treatments via bulk operation",
                createdTreatments.Count
            );

            return StatusCode(201, createdArray);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 bulk treatment data");
            return CreateV3ErrorResponse(400, "Invalid treatments data", ex.Message);
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
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v3/treatments/:id")]
    [ProducesResponseType(typeof(Treatment), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
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
    }

    /// <summary>
    /// Delete a treatment via V3 API
    /// </summary>
    /// <param name="id">Treatment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns><c>{"status": 200}</c> on success</returns>
    [HttpDelete("{id}")]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v3/treatments/:id")]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult> DeleteTreatment(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 treatment deletion requested for {Id}", id);

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

        return CreateV3DeleteResponse();
    }

    /// <summary>
    /// Get treatments modified since a given timestamp (for AAPS incremental sync).
    /// </summary>
    /// <param name="lastModified">Unix timestamp in milliseconds. Only treatments modified after this time are returned.</param>
    /// <param name="limit">Maximum number of treatments to return (1-1000, default 1000).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>V3 collection of <see cref="Treatment"/> objects modified since the given timestamp.</returns>
    /// <response code="200">Treatments modified since the given timestamp.</response>
    /// <response code="500">Internal server error.</response>
    [HttpGet("history/{lastModified:long}")]
    [NightscoutEndpoint("/api/v3/treatments/history/{lastModified}")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    [RequireScope(Scope.TreatmentsRead)]
    [ErrorEnvelope]
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

        limit = Math.Min(Math.Max(limit, 1), 1000);

        var treatments = await _treatmentService.GetTreatmentsModifiedSinceAsync(
            lastModified,
            limit,
            cancellationToken
        );

        var treatmentsList = treatments.ToList();
        if (treatmentsList.Count > 0)
        {
            SetHistoryCursorHeaders(treatmentsList.Max(t => t.SrvModified ?? t.Mills));
        }

        return CreateV3SuccessResponse(treatmentsList);
    }

    /// <summary>
    /// Partially update a treatment via V3 API (JSON merge-patch).
    /// Used by AAPS to update Temp Basal duration, endId, etc.
    /// </summary>
    /// <param name="id">The treatment ID to patch.</param>
    /// <param name="patchData">JSON merge-patch data to apply to the existing <see cref="Treatment"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The patched <see cref="Treatment"/> with updated fields.</returns>
    /// <remarks>
    /// Delegates to <see cref="ITreatmentService.PatchTreatmentAsync"/> which applies JSON merge-patch
    /// semantics. This is critical for AAPS which uses PATCH to update Temp Basal end times and durations.
    /// </remarks>
    /// <response code="200">Treatment patched successfully.</response>
    /// <response code="404">Treatment not found.</response>
    /// <response code="500">Internal server error.</response>
    [HttpPatch("{id}")]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v3/treatments/:id")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult> PatchTreatment(
        string id,
        [FromBody] JsonElement patchData,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("V3 treatment PATCH requested for {Id}", id);

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
                identifier = MongoObjectId.Coerce(result.Id),
            }
        );
    }

    #region Helper Methods


    private DateTimeOffset GetLastModified(List<Treatment> treatments)
    {
        if (treatments.Count == 0)
            return DateTimeOffset.UtcNow;

        // Use the most recent treatment's created_at as last modified
        var latestCreatedAt = treatments
            .Select(t => UploaderTimestamp.ParseUtcDateTime(t.CreatedAt))
            .OfType<DateTime>()
            .DefaultIfEmpty(DateTime.UtcNow)
            .Max();

        return new DateTimeOffset(latestCreatedAt, TimeSpan.Zero);
    }

    #endregion
}
