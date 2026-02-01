using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Controller for managing Nightscout activity data
/// Provides full CRUD operations for activity records
/// Activities are stored as StateSpans under the hood for unified data management
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public class ActivityController : ControllerBase
{
    private readonly IStateSpanService _stateSpanService;
    private readonly IDocumentProcessingService _documentProcessingService;
    private readonly ILogger<ActivityController> _logger;

    /// <summary>
    /// Initializes a new instance of the ActivityController
    /// </summary>
    /// <param name="stateSpanService">StateSpan service for data operations (activities stored as StateSpans)</param>
    /// <param name="documentProcessingService">Document processing service for sanitization and timestamp handling</param>
    /// <param name="logger">Logger instance</param>
    public ActivityController(
        IStateSpanService stateSpanService,
        IDocumentProcessingService documentProcessingService,
        ILogger<ActivityController> logger
    )
    {
        _stateSpanService =
            stateSpanService ?? throw new ArgumentNullException(nameof(stateSpanService));
        _documentProcessingService =
            documentProcessingService
            ?? throw new ArgumentNullException(nameof(documentProcessingService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Get all activities with optional filtering and pagination
    /// </summary>
    /// <param name="count">Maximum number of activities to return (default: 10)</param>
    /// <param name="skip">Number of activities to skip for pagination (default: 0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of activities</returns>
    /// <response code="200">Activities retrieved successfully</response>
    /// <response code="500">Internal server error</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Activity>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IEnumerable<Activity>>> GetActivities(
        [FromQuery] int count = 10,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Getting activities with count: {Count}, skip: {Skip}", count, skip);

            var activities = await _stateSpanService.GetActivitiesAsync(
                count: count,
                skip: skip,
                cancellationToken: cancellationToken
            );

            // Set Last-Modified header if we have activities
            var activitiesList = activities.ToList();
            if (activitiesList.Count > 0)
            {
                var latestActivity = activitiesList.FirstOrDefault();
                if (latestActivity != null && !string.IsNullOrEmpty(latestActivity.CreatedAt))
                {
                    if (DateTime.TryParse(latestActivity.CreatedAt, out var createdDate))
                    {
                        Response.Headers.Append("Last-Modified", createdDate.ToString("R"));
                    }
                }
            }

            return Ok(activitiesList);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving activities");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "An error occurred while retrieving activities" }
            );
        }
    }

    /// <summary>
    /// Get a specific activity by ID
    /// </summary>
    /// <param name="id">Activity ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The activity with the specified ID</returns>
    /// <response code="200">Activity found and returned</response>
    /// <response code="404">Activity not found</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Activity), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<Activity>> GetActivity(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Getting activity with ID: {Id}", id);

            var activity = await _stateSpanService.GetActivityByIdAsync(id, cancellationToken);
            if (activity == null)
            {
                _logger.LogDebug("Activity with ID {Id} not found", id);
                return NotFound(new { error = $"Activity with ID {id} not found" });
            }

            return Ok(activity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving activity with ID {Id}", id);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "An error occurred while retrieving the activity" }
            );
        }
    }

    /// <summary>
    /// Create one or more new activities
    /// </summary>
    /// <param name="activities">Activity data (single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created activities with assigned IDs</returns>
    /// <response code="200">Activities created successfully (Nightscout compatibility)</response>
    /// <response code="400">Invalid activity data</response>
    /// <response code="500">Internal server error</response>
    [HttpPost]
    [ProducesResponseType(typeof(IEnumerable<Activity>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IEnumerable<Activity>>> CreateActivities(
        [FromBody] object activities,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Creating activities");

            if (activities == null)
            {
                return BadRequest(new { error = "Activity data is required" });
            }

            List<Activity> activityList;

            // Handle both single activity and array of activities
            if (activities is System.Text.Json.JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    activityList =
                        System.Text.Json.JsonSerializer.Deserialize<List<Activity>>(
                            jsonElement.GetRawText()
                        ) ?? new List<Activity>();
                }
                else
                {
                    var singleActivity = System.Text.Json.JsonSerializer.Deserialize<Activity>(
                        jsonElement.GetRawText()
                    );
                    activityList =
                        singleActivity != null
                            ? new List<Activity> { singleActivity }
                            : new List<Activity>();
                }
            }
            else
            {
                return BadRequest(new { error = "Invalid activity data format" });
            }
            if (activityList.Count == 0)
            {
                return BadRequest(new { error = "At least one activity is required" });
            }

            // Process activities for sanitization and timestamp conversion
            var processedActivities = _documentProcessingService.ProcessDocuments(
                activityList
            );
            var processedList = processedActivities.ToList();

            var createdActivities = await _stateSpanService.CreateActivitiesAsync(
                processedList,
                cancellationToken
            );
            var result = createdActivities.ToList();

            _logger.LogDebug("Created {Count} activities", result.Count);
            // Nightscout returns 200 OK for POST, not 201 Created
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating activities");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "An error occurred while creating activities" }
            );
        }
    }

    /// <summary>
    /// Update an existing activity
    /// </summary>
    /// <param name="id">Activity ID to update</param>
    /// <param name="activity">Updated activity data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated activity</returns>
    /// <response code="200">Activity updated successfully</response>
    /// <response code="400">Invalid activity data</response>
    /// <response code="404">Activity not found</response>
    /// <response code="500">Internal server error</response>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Activity), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<Activity>> UpdateActivity(
        string id,
        [FromBody] Activity activity,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Updating activity with ID: {Id}", id);

            if (activity == null)
            {
                return BadRequest(new { error = "Activity data is required" });
            }

            var updatedActivity = await _stateSpanService.UpdateActivityAsync(
                id,
                activity,
                cancellationToken
            );
            if (updatedActivity == null)
            {
                _logger.LogDebug("Activity with ID {Id} not found for update", id);
                return NotFound(new { error = $"Activity with ID {id} not found" });
            }

            return Ok(updatedActivity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating activity with ID {Id}", id);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "An error occurred while updating the activity" }
            );
        }
    }

    /// <summary>
    /// Delete an activity by ID
    /// </summary>
    /// <param name="id">Activity ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Confirmation of deletion</returns>
    /// <response code="200">Activity deleted successfully</response>
    /// <response code="404">Activity not found</response>
    /// <response code="500">Internal server error</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> DeleteActivity(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Deleting activity with ID: {Id}", id);

            var deleted = await _stateSpanService.DeleteActivityAsync(id, cancellationToken);
            if (!deleted)
            {
                _logger.LogDebug("Activity with ID {Id} not found for deletion", id);
                return NotFound(new { error = $"Activity with ID {id} not found" });
            }

            return Ok(new { message = "Activity deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting activity with ID {Id}", id);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "An error occurred while deleting the activity" }
            );
        }
    }
}
