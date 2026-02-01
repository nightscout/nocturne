using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Treatments controller that provides 1:1 compatibility with Nightscout treatments endpoints
/// Implements the /api/v1/treatments/* endpoints from the legacy JavaScript implementation
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class TreatmentsController : ControllerBase
{
    private readonly ITreatmentService _treatmentService;
    private readonly IDataFormatService _dataFormatService;
    private readonly ITreatmentProcessingService _treatmentProcessingService;
    private readonly ILogger<TreatmentsController> _logger;

    public TreatmentsController(
        ITreatmentService treatmentService,
        IDataFormatService dataFormatService,
        ITreatmentProcessingService treatmentProcessingService,
        ILogger<TreatmentsController> logger
    )
    {
        _treatmentService = treatmentService;
        _dataFormatService = dataFormatService;
        _treatmentProcessingService = treatmentProcessingService;
        _logger = logger;
    }

    /// <summary>
    /// Get treatments with optional filtering and pagination
    /// </summary>
    /// <param name="find">MongoDB-style query filter for date range filtering</param>
    /// <param name="count">Maximum number of treatments to return (default: 10)</param>
    /// <param name="skip">Number of treatments to skip for pagination (default: 0)</param>
    /// <param name="format">Output format (json, csv, tsv, txt)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of treatments ordered by most recent first</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v1/treatments")]
    [ProducesResponseType(typeof(Treatment[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetTreatments(
        [FromQuery] string? find = null,
        [FromQuery] int count = 10,
        [FromQuery] int skip = 0,
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

        // Extract find query from the query string (handles multiple find parameters)
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

        _logger.LogDebug(
            "Treatments endpoint requested with count: {Count}, skip: {Skip}, findQuery: {FindQuery} from {RemoteIpAddress}",
            count,
            skip,
            findQuery,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Handle count parameter for Nightscout compatibility:
            // - 0 or negative: return empty array (Nightscout behavior)
            if (count <= 0)
            {
                _logger.LogDebug("Returning empty array for count={Count}", count);
                return Ok(Array.Empty<Treatment>());
            }

            // Validate skip parameter (negative is not valid)
            if (skip < 0)
            {
                skip = 0; // Normalize to 0 for Nightscout compatibility
            }

            var treatments = await _treatmentService.GetTreatmentsAsync(
                find: findQuery,
                count: count,
                skip: skip,
                cancellationToken: cancellationToken
            );
            var treatmentArray = treatments.ToArray();

            _logger.LogDebug("Successfully retrieved {Count} treatments", treatmentArray.Length);

            // Set response headers for caching
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");

            return Ok(treatmentArray);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving treatments");
            return StatusCode(500, "Internal server error while retrieving treatments");
        }
    }

    /// <summary>
    /// Get a specific treatment by ID
    /// </summary>
    /// <param name="id">Treatment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The treatment with the specified ID</returns>
    [HttpGet("{id}")]
    [NightscoutEndpoint("/api/v1/treatments/:id")]
    [ProducesResponseType(typeof(Treatment), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment>> GetTreatmentById(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Treatment by ID endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid treatment ID: {Id}", id);
                return BadRequest("Treatment ID cannot be null or empty");
            }

            var treatment = await _treatmentService.GetTreatmentByIdAsync(id, cancellationToken);

            if (treatment == null)
            {
                _logger.LogDebug("Treatment not found with ID: {Id}", id);
                return NotFound($"Treatment with ID '{id}' not found");
            }

            _logger.LogDebug("Successfully retrieved treatment with ID: {Id}", id);

            // Set response headers for caching
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");

            return Ok(treatment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving treatment with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while retrieving treatment {id}");
        }
    }

    /// <summary>
    /// Create new treatments
    /// </summary>
    /// <param name="treatments">Treatments to create (can be single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created treatments with assigned IDs</returns>
    [HttpPost]
    [NightscoutEndpoint("/api/v1/treatments")]
    [ProducesResponseType(typeof(Treatment[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment[]>> CreateTreatments(
        [FromBody] JsonElement treatments,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Create treatments endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            List<Treatment> treatmentsToCreate;

            // Handle both single treatment and array of treatments
            if (treatments.ValueKind == JsonValueKind.Array)
            {
                treatmentsToCreate =
                    JsonSerializer.Deserialize<List<Treatment>>(treatments.GetRawText())
                    ?? new List<Treatment>();
            }
            else if (treatments.ValueKind == JsonValueKind.Object)
            {
                var singleTreatment = JsonSerializer.Deserialize<Treatment>(
                    treatments.GetRawText()
                );
                treatmentsToCreate =
                    singleTreatment != null
                        ? new List<Treatment> { singleTreatment }
                        : new List<Treatment>();
            }
            else
            {
                _logger.LogWarning("Invalid JSON format for treatments");
                return BadRequest("Invalid JSON format. Expected object or array of treatments.");
            }
            if (treatmentsToCreate.Count == 0)
            {
                _logger.LogWarning("No treatments provided for creation");
                return BadRequest("No treatments provided");
            }

            // Process treatments: sanitize HTML, convert timestamps, set defaults, and deduplicate
            var processedTreatments = _treatmentProcessingService.ProcessTreatments(
                treatmentsToCreate
            );

            // Set default event types for treatments that don't have them
            foreach (var treatment in processedTreatments)
            {
                if (string.IsNullOrWhiteSpace(treatment.EventType))
                {
                    if (treatment.Insulin.HasValue && treatment.Carbs.HasValue)
                    {
                        treatment.EventType = "Meal Bolus";
                    }
                    else if (treatment.Insulin.HasValue)
                    {
                        treatment.EventType = "Correction Bolus";
                    }
                    else if (treatment.Carbs.HasValue)
                    {
                        treatment.EventType = "Carb Correction";
                    }
                    else
                    {
                        treatment.EventType = "Note";
                    }
                }
            }

            var createdTreatments = await _treatmentService.CreateTreatmentsAsync(
                processedTreatments,
                cancellationToken
            );
            var resultArray = createdTreatments.ToArray();

            _logger.LogDebug("Successfully created {Count} treatments", resultArray.Length);

            // Return 200 OK to match Nightscout behavior (not 201 Created)
            return Ok(resultArray);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in create treatments request");
            return BadRequest("Invalid JSON format");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating treatments");
            return StatusCode(500, "Internal server error while creating treatments");
        }
    }

    /// <summary>
    /// Update an existing treatment by ID
    /// </summary>
    /// <param name="id">Treatment ID to update</param>
    /// <param name="treatment">Updated treatment data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated treatment</returns>
    [HttpPut("{id}")]
    [NightscoutEndpoint("/api/v1/treatments/:id")]
    [ProducesResponseType(typeof(Treatment), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment>> UpdateTreatment(
        string id,
        [FromBody] Treatment treatment,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Update treatment endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid treatment ID: {Id}", id);
                return BadRequest("Treatment ID cannot be null or empty");
            }

            if (treatment == null)
            {
                _logger.LogWarning("Treatment data is null for update");
                return BadRequest("Treatment data cannot be null");
            }

            // Ensure the treatment has the correct ID
            treatment.Id = id;

            // Update timestamp if mills is provided but created_at is not
            if (treatment.Mills > 0 && string.IsNullOrWhiteSpace(treatment.CreatedAt))
            {
                var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills);
                treatment.CreatedAt = dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
            else if (treatment.Mills <= 0 && !string.IsNullOrWhiteSpace(treatment.CreatedAt))
            {
                treatment.Mills = treatment.CalculatedMills;
            }

            var updatedTreatment = await _treatmentService.UpdateTreatmentAsync(
                id,
                treatment,
                cancellationToken
            );

            if (updatedTreatment == null)
            {
                _logger.LogDebug("Treatment not found for update with ID: {Id}", id);
                return NotFound($"Treatment with ID '{id}' not found");
            }

            _logger.LogDebug("Successfully updated treatment with ID: {Id}", id);

            return Ok(updatedTreatment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating treatment with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while updating treatment {id}");
        }
    }

    /// <summary>
    /// Delete a treatment by ID
    /// </summary>
    /// <param name="id">Treatment ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    [HttpDelete("{id}")]
    [NightscoutEndpoint("/api/v1/treatments/:id")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteTreatment(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Delete treatment endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid treatment ID: {Id}", id);
                return BadRequest("Treatment ID cannot be null or empty");
            }

            var deleted = await _treatmentService.DeleteTreatmentAsync(id, cancellationToken);

            if (!deleted)
            {
                _logger.LogDebug("Treatment not found for deletion with ID: {Id}", id);
                return NotFound($"Treatment with ID '{id}' not found");
            }

            _logger.LogDebug("Successfully deleted treatment with ID: {Id}", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting treatment with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while deleting treatment {id}");
        }
    }

    /// <summary>
    /// Bulk delete treatments using query parameters
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of treatments deleted</returns>
    [HttpDelete]
    [NightscoutEndpoint("/api/v1/treatments")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> BulkDeleteTreatments(
        CancellationToken cancellationToken = default
    )
    {
        // Parse the full query string to reconstruct the find query
        var queryString = HttpContext?.Request?.QueryString.ToString() ?? string.Empty;

        // Strip the leading '?' if present
        if (queryString.StartsWith("?"))
        {
            queryString = queryString.Substring(1);
        }

        _logger.LogDebug(
            "Bulk delete treatments endpoint requested with query: {QueryString} from {RemoteIpAddress}",
            queryString,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Check if there are any find parameters in the query string
            if (string.IsNullOrWhiteSpace(queryString) || !queryString.Contains("find["))
            {
                _logger.LogWarning("Bulk delete requested without find query - this is dangerous");
                return BadRequest(
                    "Find query parameter is required for bulk delete to prevent accidental data loss"
                );
            }

            var deletedCount = await _treatmentService.DeleteTreatmentsAsync(
                queryString,
                cancellationToken
            );

            _logger.LogDebug("Successfully deleted {Count} treatments", deletedCount);

            // Return result in the same format as Nightscout legacy API
            // Nightscout returns MongoDB driver result which includes result object, n, and ok
            // Use Dictionary to ensure 'n' is always serialized even when 0 (WhenWritingDefault would omit it)
            var response = new Dictionary<string, object>
            {
                ["result"] = new Dictionary<string, object> { ["n"] = deletedCount, ["ok"] = 1 },
                ["n"] = deletedCount,
                ["ok"] = 1
            };
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error bulk deleting treatments with query: {QueryString}",
                queryString
            );
            return StatusCode(500, "Internal server error while bulk deleting treatments");
        }
    }
}
