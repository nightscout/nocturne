using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// V4 Treatments controller with authentication and tracker integration.
/// Unlike V1-V3 endpoints, this does NOT include StateSpan-derived basal data.
/// For basal delivery data, use /api/v4/state-spans?category=BasalDelivery instead.
/// This provides a clean separation of concerns:
/// - V4 treatments: boluses, site changes, notes, etc.
/// - StateSpans: basal delivery, temp basals, pump modes, etc.
/// </summary>
[ApiController]
[Route("api/v4/treatments")]
[Tags("V4 Treatments")]
[Authorize]
public class TreatmentsController : ControllerBase
{
    private readonly TreatmentRepository _repository;
    private readonly IDocumentProcessingService _documentProcessingService;
    private readonly ITrackerTriggerService _trackerTriggerService;
    private readonly ITrackerSuggestionService _trackerSuggestionService;
    private readonly ISignalRBroadcastService _broadcast;
    private readonly ILogger<TreatmentsController> _logger;

    public TreatmentsController(
        TreatmentRepository repository,
        IDocumentProcessingService documentProcessingService,
        ITrackerTriggerService trackerTriggerService,
        ITrackerSuggestionService trackerSuggestionService,
        ISignalRBroadcastService broadcast,
        ILogger<TreatmentsController> logger
    )
    {
        _repository = repository;
        _documentProcessingService = documentProcessingService;
        _trackerTriggerService = trackerTriggerService;
        _trackerSuggestionService = trackerSuggestionService;
        _broadcast = broadcast;
        _logger = logger;
    }

    /// <summary>
    /// Get treatments with optional filtering and pagination.
    /// Unlike V1-V3 endpoints, this does NOT include StateSpan-derived basal data.
    /// For basal delivery, query /api/v4/state-spans?category=BasalDelivery instead.
    /// </summary>
    /// <param name="eventType">Optional filter by event type</param>
    /// <param name="count">Maximum number of treatments to return (default: 100)</param>
    /// <param name="skip">Number of treatments to skip for pagination (default: 0)</param>
    /// <param name="findQuery">Optional MongoDB-style query filter for advanced filtering</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of treatments ordered by most recent first</returns>
    [HttpGet]
    [AllowAnonymous]
    [RemoteQuery]
    [ProducesResponseType(typeof(Treatment[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<Treatment[]>> GetTreatments(
        [FromQuery] string? eventType = null,
        [FromQuery] int count = 100,
        [FromQuery] int skip = 0,
        [FromQuery(Name = "find")] string? findQuery = null,
        CancellationToken cancellationToken = default
    )
    {
        // Validate parameters
        if (count <= 0)
        {
            return Ok(Array.Empty<Treatment>());
        }

        if (skip < 0)
        {
            skip = 0;
        }

        // Use repository directly - this bypasses the service layer's StateSpan merging
        // V4 clients should query StateSpans separately for basal delivery data
        var treatments = await _repository.GetTreatmentsWithAdvancedFilterAsync(
            eventType: eventType,
            count: count,
            skip: skip,
            findQuery: findQuery,
            reverseResults: false,
            cancellationToken: cancellationToken
        );

        _logger.LogDebug(
            "V4 GetTreatments returned {Count} treatments (eventType: {EventType}, count: {RequestedCount}, skip: {Skip})",
            treatments.Count(),
            eventType ?? "all",
            count,
            skip
        );

        return Ok(treatments.ToArray());
    }

    /// <summary>
    /// Create a treatment with tracker integration.
    /// If the treatment's event type matches a tracker's trigger event types,
    /// the tracker instance will be automatically started/restarted.
    /// </summary>
    [HttpPost]
    [RemoteCommand(Invalidates = ["GetTreatments"])]
    [ProducesResponseType(typeof(Treatment), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Treatment>> CreateTreatment(
        [FromBody] Treatment treatment,
        CancellationToken cancellationToken = default
    )
    {
        if (treatment == null)
            return BadRequest("Treatment data is required");

        var userId = HttpContext.GetSubjectIdString()!;

        // Process the treatment (adds timestamps, etc.)
        var processedTreatment = _documentProcessingService.ProcessTreatment(treatment);

        // Save to database
        var created = await _repository.CreateTreatmentAsync(processedTreatment, cancellationToken);

        if (created == null)
            return StatusCode(500, "Failed to create treatment");

        _logger.LogInformation(
            "Created V4 treatment {Id} ({EventType}) for user {UserId}",
            created.Id,
            created.EventType,
            userId
        );

        // Trigger any matching trackers
        await _trackerTriggerService.ProcessTreatmentAsync(created, userId, cancellationToken);

        // Evaluate for tracker suggestions (e.g., Site Change -> suggest resetting Cannula tracker)
        await _trackerSuggestionService.EvaluateTreatmentForTrackerSuggestionAsync(created, userId, cancellationToken);

        // Broadcast via SignalR
        await _broadcast.BroadcastStorageCreateAsync("treatments", created);

        return CreatedAtAction(nameof(GetTreatment), new { id = created.Id }, created);
    }

    /// <summary>
    /// Create multiple treatments with tracker integration.
    /// </summary>
    [HttpPost("bulk")]
    [RemoteCommand(Invalidates = ["GetTreatments"])]
    [ProducesResponseType(typeof(Treatment[]), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Treatment[]>> CreateTreatments(
        [FromBody] Treatment[] treatments,
        CancellationToken cancellationToken = default
    )
    {
        if (treatments == null || treatments.Length == 0)
            return BadRequest("Treatment data is required");

        if (treatments.Length > 1000)
            return BadRequest("Bulk operations are limited to 1000 treatments per request");

        var userId = HttpContext.GetSubjectIdString()!;

        // Process all treatments
        var processedTreatments = treatments
            .Select(t => _documentProcessingService.ProcessTreatment(t))
            .ToList();

        // Save to database
        var created = await _repository.CreateTreatmentsAsync(
            processedTreatments,
            cancellationToken
        );
        var createdArray = created.ToArray();

        _logger.LogInformation(
            "Created {Count} V4 treatments for user {UserId}",
            createdArray.Length,
            userId
        );

        // Trigger any matching trackers
        await _trackerTriggerService.ProcessTreatmentsAsync(
            createdArray,
            userId,
            cancellationToken
        );

        // Evaluate for tracker suggestions (e.g., Site Change -> suggest resetting Cannula tracker)
        foreach (var treatment in createdArray)
        {
            await _trackerSuggestionService.EvaluateTreatmentForTrackerSuggestionAsync(treatment, userId, cancellationToken);
        }

        // Broadcast via SignalR
        foreach (var treatment in createdArray)
        {
            await _broadcast.BroadcastStorageCreateAsync("treatments", treatment);
        }

        return StatusCode(201, createdArray);
    }

    /// <summary>
    /// Get a specific treatment by ID
    /// </summary>
    [HttpGet("{id}")]
    [AllowAnonymous]
    [RemoteQuery]
    [ProducesResponseType(typeof(Treatment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Treatment>> GetTreatment(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        var treatment = await _repository.GetTreatmentByIdAsync(id, cancellationToken);

        if (treatment == null)
            return NotFound();

        return Ok(treatment);
    }

    /// <summary>
    /// Update an existing treatment by ID
    /// </summary>
    [HttpPut("{id}")]
    [RemoteCommand(Invalidates = ["GetTreatments", "GetTreatment"])]
    [ProducesResponseType(typeof(Treatment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Treatment>> UpdateTreatment(
        string id,
        [FromBody] Treatment treatment,
        CancellationToken cancellationToken = default
    )
    {
        if (treatment == null)
            return BadRequest("Treatment data is required");

        // Ensure the treatment has the correct ID
        treatment.Id = id;

        var updated = await _repository.UpdateTreatmentAsync(id, treatment, cancellationToken);

        if (updated == null)
            return NotFound($"Treatment with ID '{id}' not found");

        _logger.LogInformation(
            "Updated V4 treatment {Id} ({EventType})",
            updated.Id,
            updated.EventType
        );

        // Broadcast via SignalR
        await _broadcast.BroadcastStorageUpdateAsync("treatments", updated);

        return Ok(updated);
    }

    /// <summary>
    /// Delete a treatment by ID
    /// </summary>
    [HttpDelete("{id}")]
    [RemoteCommand(Invalidates = ["GetTreatments"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTreatment(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Get the treatment before deleting for broadcasting
        var treatmentToDelete = await _repository.GetTreatmentByIdAsync(id, cancellationToken);

        if (treatmentToDelete == null)
            return NotFound($"Treatment with ID '{id}' not found");

        var deleted = await _repository.DeleteTreatmentAsync(id, cancellationToken);

        if (!deleted)
            return NotFound($"Treatment with ID '{id}' not found");

        _logger.LogInformation("Deleted V4 treatment {Id}", id);

        // Broadcast via SignalR
        await _broadcast.BroadcastStorageDeleteAsync("treatments", treatmentToDelete);

        return NoContent();
    }
}
