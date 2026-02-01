using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for debug and query inspection endpoints
/// Provides query debugging and MongoDB query inspection capabilities
/// </summary>
[ApiController]
[Route("api/v4/debug")]
[Produces("application/json")]
[Tags("V4 Debug")]
public class DebugController : ControllerBase
{
    private readonly IPostgreSqlService _postgreSqlService;
    private readonly IInAppNotificationService _notificationService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DebugController> _logger;

    /// <summary>
    /// Initializes a new instance of the DebugController
    /// </summary>
    /// <param name="postgreSqlService">PostgreSQL service for data operations</param>
    /// <param name="notificationService">In-app notification service</param>
    /// <param name="environment">Web host environment</param>
    /// <param name="logger">Logger instance</param>
    public DebugController(
        IPostgreSqlService postgreSqlService,
        IInAppNotificationService notificationService,
        IWebHostEnvironment environment,
        ILogger<DebugController> logger
    )
    {
        _postgreSqlService =
            postgreSqlService ?? throw new ArgumentNullException(nameof(postgreSqlService));
        _notificationService =
            notificationService ?? throw new ArgumentNullException(nameof(notificationService));
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
                    BadRequest(new { error = "Entry data is required" })
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
                    BadRequest(new { error = "Invalid entry data format" })
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

    /// <summary>
    /// Test endpoint for creating in-app notifications (development only)
    /// Creates a test notification for the current user to verify the notification system
    /// </summary>
    /// <param name="request">The test notification parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created notification</returns>
    /// <response code="200">Notification created successfully</response>
    /// <response code="400">Invalid request parameters</response>
    /// <response code="401">User not authenticated</response>
    /// <response code="403">Endpoint only available in development</response>
    [HttpPost("test/inappnotification")]
    [Authorize]
    [ProducesResponseType(typeof(InAppNotificationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<InAppNotificationDto>> CreateTestNotification(
        [FromBody] TestNotificationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        // if (!_environment.IsDevelopment())
        // {
        //     return StatusCode(
        //         StatusCodes.Status403Forbidden,
        //         new { error = "This endpoint is only available in development mode" }
        //     );
        // }

        var userId = HttpContext.GetSubjectIdString();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        _logger.LogInformation(
            "Creating test notification of type {Type} with urgency {Urgency} for user {UserId}",
            request.Type,
            request.Urgency,
            userId
        );

        var notification = await _notificationService.CreateNotificationAsync(
            userId,
            request.Type,
            request.Urgency,
            request.Title ?? $"Test {request.Type} Notification",
            request.Subtitle,
            request.SourceId,
            request.Actions,
            request.ResolutionConditions,
            request.Metadata,
            cancellationToken
        );

        return Ok(notification);
    }

    /// <summary>
    /// Simple test endpoint for creating in-app notifications without authentication
    /// Creates a test notification to verify the real-time notification system is working
    /// </summary>
    /// <param name="type">Notification type (info, warn, hazard, urgent)</param>
    /// <param name="title">Optional notification title</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created notification</returns>
    /// <response code="200">Notification created and broadcast successfully</response>
    [HttpGet("test/notification")]
    [ProducesResponseType(typeof(InAppNotificationDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<InAppNotificationDto>> CreateSimpleTestNotification(
        [FromQuery] string type = "info",
        [FromQuery] string? title = null,
        CancellationToken cancellationToken = default
    )
    {
        // Use default user ID for unauthenticated testing
        var userId = "00000000-0000-0000-0000-000000000001";

        var urgency = type.ToLowerInvariant() switch
        {
            "urgent" => NotificationUrgency.Urgent,
            "hazard" => NotificationUrgency.Hazard,
            "warning" or "warn" => NotificationUrgency.Warn,
            _ => NotificationUrgency.Info
        };

        var notificationType = type.ToLowerInvariant() switch
        {
            "tracker" => InAppNotificationType.TrackerAlert,
            "stats" or "statistics" => InAppNotificationType.StatisticsSummary,
            "low" or "predicted" => InAppNotificationType.PredictedLow,
            "meal" => InAppNotificationType.SuggestedMealMatch,
            "help" => InAppNotificationType.HelpResponse,
            _ => InAppNotificationType.TrackerAlert
        };

        var notificationTitle = title ?? $"Test Notification ({DateTime.UtcNow:HH:mm:ss})";

        _logger.LogInformation(
            "Creating simple test notification: type={Type}, urgency={Urgency}, title={Title}",
            notificationType,
            urgency,
            notificationTitle
        );

        var notification = await _notificationService.CreateNotificationAsync(
            userId,
            notificationType,
            urgency,
            notificationTitle,
            subtitle: $"This is a test notification created at {DateTime.UtcNow:u}",
            sourceId: $"test-{Guid.NewGuid():N}",
            actions: null,
            resolutionConditions: null,
            metadata: new Dictionary<string, object>
            {
                ["testCreatedAt"] = DateTime.UtcNow.ToString("o"),
                ["testType"] = type
            },
            cancellationToken
        );

        return Ok(new
        {
            message = "Notification created and broadcast via SignalR",
            notification,
            testInstructions = new
            {
                step1 = "Open the Nocturne web app in your browser",
                step2 = "Check the notification bell icon in the sidebar",
                step3 = "You should see this notification appear in real-time",
                step4 = "If it doesn't appear, check browser console for WebSocket errors"
            }
        });
    }

    /// <summary>
    /// Test endpoint to broadcast a raw SignalR notification event
    /// This bypasses the database and directly tests the SignalR broadcast
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Confirmation of broadcast</returns>
    [HttpGet("test/signalr-broadcast")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> TestSignalRBroadcast(
        CancellationToken cancellationToken = default
    )
    {
        var userId = "00000000-0000-0000-0000-000000000001";
        var testNotification = new InAppNotificationDto
        {
            Id = Guid.NewGuid(),
            Type = InAppNotificationType.TrackerAlert,
            Urgency = NotificationUrgency.Info,
            Title = $"SignalR Test ({DateTime.UtcNow:HH:mm:ss})",
            Subtitle = "Testing direct SignalR broadcast",
            CreatedAt = DateTime.UtcNow
        };

        _logger.LogInformation("Broadcasting test notification directly via SignalR");

        // Get SignalR broadcast service and send directly
        var signalRService = HttpContext.RequestServices.GetRequiredService<ISignalRBroadcastService>();
        await signalRService.BroadcastNotificationCreatedAsync(userId, testNotification);

        return Ok(new
        {
            message = "Notification broadcast directly via SignalR (not saved to DB)",
            notification = testNotification,
            note = "This tests the SignalR -> Bridge -> Socket.IO -> Frontend path"
        });
    }
}

/// <summary>
/// Request model for creating test notifications
/// </summary>
public class TestNotificationRequest
{
    /// <summary>
    /// Type of notification to create
    /// </summary>
    public InAppNotificationType Type { get; set; } = InAppNotificationType.TrackerAlert;

    /// <summary>
    /// Urgency level for the notification
    /// </summary>
    public NotificationUrgency Urgency { get; set; } = NotificationUrgency.Info;

    /// <summary>
    /// Optional title (defaults to "Test {Type} Notification")
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Optional subtitle
    /// </summary>
    public string? Subtitle { get; set; }

    /// <summary>
    /// Optional source ID for grouping
    /// </summary>
    public string? SourceId { get; set; }

    /// <summary>
    /// Optional actions for the notification
    /// </summary>
    public List<NotificationActionDto>? Actions { get; set; }

    /// <summary>
    /// Optional resolution conditions
    /// </summary>
    public ResolutionConditions? ResolutionConditions { get; set; }

    /// <summary>
    /// Optional metadata
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }
}
