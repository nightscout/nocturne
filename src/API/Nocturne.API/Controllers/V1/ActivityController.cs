using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Controller for managing Nightscout activity data.
/// Delegates to <see cref="IActivityService"/> which routes sensor data (heart rate, step count)
/// to dedicated tables and regular activities to StateSpans.
/// </summary>
/// <seealso cref="IActivityService"/>
[ApiController]
[Tags("V1")]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class ActivityController : ControllerBase
{
    private readonly IActivityService _activityService;
    private readonly IActivityDecomposer _activityDecomposer;
    private readonly ILogger<ActivityController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ActivityController"/>.
    /// </summary>
    /// <param name="activityService">Service handling activity data operations.</param>
    /// <param name="activityDecomposer">Classifier mapping each activity to its required write scope.</param>
    /// <param name="logger">Logger instance.</param>
    public ActivityController(
        IActivityService activityService,
        IActivityDecomposer activityDecomposer,
        ILogger<ActivityController> logger
    )
    {
        _activityService =
            activityService ?? throw new ArgumentNullException(nameof(activityService));
        _activityDecomposer =
            activityDecomposer ?? throw new ArgumentNullException(nameof(activityDecomposer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Get all activities with optional filtering and pagination.
    /// Returns regular activities, heart rate, and step count data merged by timestamp.
    /// </summary>
    /// <remarks>
    /// The scope requirement is an OR because the response merges four storages, each with its own
    /// read scope. Admission means the caller holds at least one of those categories;
    /// <see cref="ActivityReadScopeGuard"/> then removes the records whose category the caller does
    /// not hold. Pagination is applied by the service before the filter, so a caller holding a
    /// subset of the categories can receive fewer than <paramref name="count"/> records.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Activity>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [RequireScope(
        OAuthScopes.TreatmentsRead,
        OAuthScopes.HeartRateRead,
        OAuthScopes.StepCountRead,
        OAuthScopes.SleepRead)]
    public async Task<ActionResult<IEnumerable<Activity>>> GetActivities(
        [FromQuery] int count = 10,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var activities = await _activityService.GetActivitiesAsync(
                count: count,
                skip: skip,
                cancellationToken: cancellationToken
            );

            var activitiesList = ActivityReadScopeGuard.Filter(
                activities, _activityDecomposer, HttpContext.GetGrantedScopes());
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
    /// Get a specific activity by ID (checks StateSpans, sleep_sessions, heart_rates, and step_counts)
    /// </summary>
    /// <remarks>
    /// The scope requirement is an OR over the four storages this route resolves against, and
    /// <see cref="ActivityReadScopeGuard"/> then checks the resolved record's own category. A record
    /// in a category the caller does not hold answers 404 rather than 403, so the response does not
    /// disclose that the record exists.
    /// </remarks>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Activity), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [RequireScope(
        OAuthScopes.TreatmentsRead,
        OAuthScopes.HeartRateRead,
        OAuthScopes.StepCountRead,
        OAuthScopes.SleepRead)]
    public async Task<ActionResult<Activity>> GetActivity(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var activity = await _activityService.GetActivityByIdAsync(id, cancellationToken);
            if (activity == null)
                return NotFound(new { error = $"Activity with ID {id} not found" });

            if (!ActivityReadScopeGuard.CanRead(
                activity, _activityDecomposer, HttpContext.GetGrantedScopes()))
                return NotFound(new { error = $"Activity with ID {id} not found" });

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
    /// Create one or more new activities.
    /// Heart rate and step count data is automatically routed to dedicated tables.
    /// </summary>
    [HttpPost]
    [Authorize]
    [RequireScope(OAuthScopes.TreatmentsReadWrite)]
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
            if (activities == null)
                return BadRequest(new { error = "Activity data is required" });

            List<Activity> activityList;

            if (activities is System.Text.Json.JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    activityList =
                        System.Text.Json.JsonSerializer.Deserialize<List<Activity>>(
                            jsonElement.GetRawText()
                        ) ?? [];
                }
                else
                {
                    var singleActivity = System.Text.Json.JsonSerializer.Deserialize<Activity>(
                        jsonElement.GetRawText()
                    );
                    activityList = singleActivity != null ? [singleActivity] : [];
                }
            }
            else
            {
                return BadRequest(new { error = "Invalid activity data format" });
            }

            if (activityList.Count == 0)
                return BadRequest(new { error = "At least one activity is required" });

            var missingScope = ActivityWriteScopeGuard.FindMissingScope(
                activityList, _activityDecomposer, HttpContext.GetGrantedScopes());
            if (missingScope is not null)
                return ForbiddenForScope(missingScope);

            // ActivityService handles document processing, routing, and broadcasting
            var result = await _activityService.CreateActivitiesAsync(
                activityList,
                cancellationToken
            );

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
    [HttpPut("{id}")]
    [Authorize]
    [RequireScope(OAuthScopes.TreatmentsReadWrite)]
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
            if (activity == null)
                return BadRequest(new { error = "Activity data is required" });

            // Gate on both the payload's destination and the existing record's destination so a
            // caller cannot write or edit sleep/heart-rate/step data without its category scope.
            var existing = await _activityService.GetActivityByIdAsync(id, cancellationToken);
            var toCheck = new List<Activity> { activity };
            if (existing is not null)
                toCheck.Add(existing);
            var missingScope = ActivityWriteScopeGuard.FindMissingScope(
                toCheck, _activityDecomposer, HttpContext.GetGrantedScopes());
            if (missingScope is not null)
                return ForbiddenForScope(missingScope);

            var updatedActivity = await _activityService.UpdateActivityAsync(
                id,
                activity,
                cancellationToken
            );

            if (updatedActivity == null)
                return NotFound(new { error = $"Activity with ID {id} not found" });

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
    /// Delete an activity by ID (also deletes any decomposed heart rate / step count records)
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize]
    [RequireScope(OAuthScopes.FullAccess)]
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
            var deleted = await _activityService.DeleteActivityAsync(id, cancellationToken);
            if (!deleted)
                return NotFound(new { error = $"Activity with ID {id} not found" });

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

    private ObjectResult ForbiddenForScope(string scope) => StatusCode(
        StatusCodes.Status403Forbidden,
        new { error = $"This operation requires the '{scope}' scope." }
    );
}
