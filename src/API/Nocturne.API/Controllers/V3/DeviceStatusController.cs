using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// V3 DeviceStatus controller that provides full V3 API compatibility with Nightscout devicestatus endpoints.
/// Writes decompose directly into V4 snapshot tables; reads project back from V4.
/// </summary>
/// <seealso cref="IDeviceStatusDecomposer"/>
/// <seealso cref="DeviceStatusProjectionService"/>
/// <seealso cref="BaseV3Controller{T}"/>
[ApiController]
[Route("api/v3/[controller]")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class DeviceStatusController : BaseV3Controller<DeviceStatus>
{
    private readonly DeviceStatusProjectionService _projection;
    private readonly IDeviceStatusDecomposer _decomposer;
    private readonly IWriteSideEffects _sideEffects;
    private readonly IDataEventSink<DeviceStatus> _events;

    private const string CollectionName = "devicestatus";

    /// <summary>
    /// Suppress V4 decomposition in side effects — the controller decomposes directly.
    /// </summary>
    private static readonly WriteEffectOptions NoDecompose = new() { DecomposeToV4 = false };

    public DeviceStatusController(
        DeviceStatusProjectionService projection,
        IDeviceStatusDecomposer decomposer,
        IWriteSideEffects sideEffects,
        IDataEventSink<DeviceStatus> events,
        IDocumentProcessingService documentProcessingService,
        ILogger<DeviceStatusController> logger
    )
        : base(documentProcessingService, logger)
    {
        _projection = projection;
        _decomposer = decomposer;
        _sideEffects = sideEffects;
        _events = events;
    }

    /// <summary>
    /// Get device status records with V3 API features including pagination, field selection, and advanced filtering
    /// </summary>
    /// <returns>V3 device status collection response</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v3/devicestatus")]
    [ProducesResponseType(typeof(V3CollectionResponse<object>), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(304)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> GetDeviceStatus(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "V3 devicestatus endpoint requested from {RemoteIpAddress}",
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
            var query = HttpContext!.Request.Query;
            var hasSortDesc = query.ContainsKey("sort$desc");
            var reverseResults = !hasSortDesc && ExtractSortDirection(parameters.Sort);

            // Project device status from V4 snapshot tables
            var deviceStatusListRaw = await _projection.GetAsync(
                parameters.Limit,
                parameters.Offset,
                findQuery,
                cancellationToken
            );

            var deviceStatusList = deviceStatusListRaw.ToList();

            // Reverse results if needed (projection returns newest-first by default)
            if (reverseResults)
            {
                deviceStatusList.Reverse();
            }

            var totalCount = await _projection.CountAsync(findQuery, cancellationToken);

            var mappedData = deviceStatusList.Select(MapToV3Dto);

            // Check for conditional requests (304 Not Modified)
            var lastModified = GetLastModified(deviceStatusList.Cast<object>());
            var etag = GenerateETag(deviceStatusList);

            if (lastModified.HasValue && ShouldReturn304(etag, lastModified.Value, parameters))
            {
                return StatusCode(304);
            }

            // Create V3 response
            var response = CreateV3CollectionResponse(mappedData, parameters, totalCount);

            _logger.LogDebug(
                "Successfully returned {Count} device status records with V3 format",
                deviceStatusList.Count
            );

            return response;
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 devicestatus request parameters");
            return CreateV3ErrorResponse(400, "Invalid request parameters", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving V3 devicestatus");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Get a specific device status record by ID with V3 format
    /// </summary>
    /// <param name="id">Device status ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Single device status record in V3 format</returns>
    [HttpGet("{id}")]
    [NightscoutEndpoint("/api/v3/devicestatus/{id}")]
    [ProducesResponseType(typeof(DeviceStatus), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetDeviceStatusById(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 devicestatus by ID endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var result = await _projection.GetByIdAsync(id, cancellationToken);

            if (result == null)
            {
                return CreateV3ErrorResponse(404, "Device status not found");
            }

            return CreateV3SuccessResponse(MapToV3Dto(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving device status with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Create new device status records with V3 format and deduplication support
    /// </summary>
    /// <param name="deviceStatusData">Device status data to create (single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created device status records</returns>
    [HttpPost]
    [Authorize]
    [NightscoutEndpoint("/api/v3/devicestatus")]
    [ProducesResponseType(typeof(DeviceStatus[]), 201)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> CreateDeviceStatus(
        [FromBody] JsonElement deviceStatusData,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 devicestatus create endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );
        try
        {
            if (
                deviceStatusData.ValueKind == JsonValueKind.Object
                && !deviceStatusData.EnumerateObject().Any()
            )
            {
                return CreateV3ErrorResponse(400, "Bad or missing request body");
            }

            var deviceStatusRecords = ParseCreateRequestFromJsonElement(
                deviceStatusData,
                out var validationError
            );

            if (validationError != null)
            {
                return validationError;
            }

            if (!deviceStatusRecords.Any())
            {
                return CreateV3ErrorResponse(
                    400,
                    "Invalid request body",
                    "Request body must contain valid device status data"
                );
            }

            // Process each device status record (date parsing, validation, etc.)
            foreach (var deviceStatus in deviceStatusRecords)
            {
                // Nightscout V3 Strict Validation: device field is required
                if (string.IsNullOrWhiteSpace(deviceStatus.Device))
                {
                    return CreateV3ErrorResponse(
                        400,
                        "Bad or missing app field", // Exact Nightscout error message
                        "Device name is required"
                    );
                }
                ProcessDeviceStatusForCreation(deviceStatus);
            }

            // For single record POSTs, check for deduplication (AAPS expects isDeduplication response)
            var recordsList = deviceStatusRecords.ToList();
            if (recordsList.Count == 1)
            {
                var single = recordsList[0];
                if (!string.IsNullOrEmpty(single.Id))
                {
                    var existing = await _projection.GetByIdAsync(
                        single.Id,
                        cancellationToken
                    );
                    if (existing != null)
                    {
                        return Ok(
                            new
                            {
                                status = 200,
                                identifier = existing.Id,
                                isDeduplication = true,
                                deduplicatedIdentifier = existing.Id,
                            }
                        );
                    }
                }
            }

            // Decompose each device status directly into V4 snapshot tables
            var projectedResults = new List<DeviceStatus>();
            foreach (var ds in recordsList)
            {
                await _decomposer.DecomposeAsync(ds, cancellationToken);

                // Project the V4 snapshots back to DeviceStatus shape for the response
                var projected = ds;
                if (!string.IsNullOrEmpty(ds.Id))
                {
                    var fromV4 = await _projection.GetByIdAsync(ds.Id, cancellationToken);
                    if (fromV4 != null)
                        projected = fromV4;
                }

                projectedResults.Add(projected);
            }

            // Broadcast via WriteSideEffectsService (cache invalidation + SignalR)
            await _sideEffects.OnCreatedAsync(
                CollectionName,
                projectedResults,
                NoDecompose,
                cancellationToken
            );

            await _events.OnCreatedAsync(projectedResults, cancellationToken);

            return CreatedAtAction(
                nameof(GetDeviceStatusById),
                new { id = projectedResults.First().Id },
                new { status = 200, result = projectedResults.Select(MapToV3Dto) }
            );
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 devicestatus create request");
            return CreateV3ErrorResponse(400, "Invalid request data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating V3 devicestatus");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Update a device status record by ID with V3 format.
    /// Deletes old V4 records, decomposes the updated DeviceStatus, and projects back.
    /// </summary>
    /// <param name="id">Device status ID to update</param>
    /// <param name="request">Updated device status data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated device status record</returns>
    [HttpPut("{id}")]
    [Authorize]
    [NightscoutEndpoint("/api/v3/devicestatus/{id}")]
    [ProducesResponseType(typeof(Dictionary<string, object>), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    public async Task<IActionResult> UpdateDeviceStatus(
        string id,
        [FromBody] JsonElement request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Updating device status {Id}", id);

        // Nightscout V3 Strict Validation: app field is required for PUT
        if (!request.TryGetProperty("app", out _))
        {
            return CreateV3ErrorResponse(400, "Bad or missing app field");
        }

        DeviceStatus? deviceStatus;
        try
        {
            deviceStatus = JsonSerializer.Deserialize<DeviceStatus>(
                request.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
        }
        catch (JsonException)
        {
            return CreateV3ErrorResponse(400, "Invalid JSON format");
        }

        if (deviceStatus == null)
        {
            return CreateV3ErrorResponse(
                400,
                "Bad or missing app field",
                "Request body must contain valid device status data"
            );
        }

        // Ensure ID matches
        if (deviceStatus.Id != null && deviceStatus.Id != id)
        {
            return CreateV3ErrorResponse(400, "ID mismatch");
        }

        deviceStatus.Id = id;
        ProcessDeviceStatusForCreation(deviceStatus);

        try
        {
            // Verify the record exists in V4 before updating
            var existing = await _projection.GetByIdAsync(id, cancellationToken);
            if (existing == null)
            {
                return CreateV3ErrorResponse(404, "Device status not found");
            }

            // Delete old V4 records by legacy ID, then decompose the updated DeviceStatus
            await _decomposer.DeleteByLegacyIdAsync(id, cancellationToken);
            await _decomposer.DecomposeAsync(deviceStatus, cancellationToken);

            // Project the V4 snapshots back to DeviceStatus shape for the response
            var updated = await _projection.GetByIdAsync(id, cancellationToken) ?? deviceStatus;

            // Broadcast via WriteSideEffectsService (cache invalidation + SignalR)
            await _sideEffects.OnUpdatedAsync(
                CollectionName,
                updated,
                NoDecompose,
                cancellationToken
            );

            await _events.OnUpdatedAsync(updated, cancellationToken);

            return Ok(
                new Dictionary<string, object>
                {
                    ["status"] = 200,
                    ["result"] = MapToV3Dto(updated),
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating device status with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Delete a device status record by ID
    /// </summary>
    /// <param name="id">Device status ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    [HttpDelete("{id}")]
    [Authorize]
    [NightscoutEndpoint("/api/v3/devicestatus/{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteDeviceStatus(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 devicestatus delete endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Get the projected record before deleting (for broadcast)
            var deviceStatusToDelete = await _projection.GetByIdAsync(id, cancellationToken);

            // Delete V4 snapshot records by legacy ID
            var deleted = await _decomposer.DeleteByLegacyIdAsync(id, cancellationToken);

            if (deleted == 0 && deviceStatusToDelete == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Device status not found",
                    $"Device status with ID '{id}' was not found"
                );
            }

            await _sideEffects.OnDeletedAsync(
                CollectionName,
                deviceStatusToDelete,
                NoDecompose,
                cancellationToken
            );

            await _events.OnDeletedAsync(deviceStatusToDelete, cancellationToken);

            _logger.LogDebug("Successfully deleted device status with ID {Id}", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting device status with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Get device status records modified since a given timestamp (for AAPS incremental sync).
    /// </summary>
    /// <param name="lastModified">Unix timestamp in milliseconds. Only records modified after this time are returned.</param>
    /// <param name="limit">Maximum number of records to return (1-1000, default 1000).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>V3 collection of <see cref="DeviceStatus"/> objects modified since the given timestamp.</returns>
    [HttpGet("history/{lastModified:long}")]
    [NightscoutEndpoint("/api/v3/devicestatus/history/{lastModified}")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetDeviceStatusHistory(
        long lastModified,
        [FromQuery] int limit = 1000,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 devicestatus history requested since {LastModified} with limit {Limit}",
            lastModified,
            limit
        );

        try
        {
            limit = Math.Min(Math.Max(limit, 1), 1000);

            var deviceStatuses = await _projection.GetModifiedSinceAsync(
                lastModified,
                limit,
                cancellationToken
            );
            var mappedData = deviceStatuses.Select(MapToV3Dto);
            return CreateV3SuccessResponse(mappedData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving devicestatus history");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Process device status for creation/update (date parsing, validation, etc.)
    /// Follows the legacy API v3 behavior exactly
    /// </summary>
    /// <param name="deviceStatus">Device status to process</param>
    private void ProcessDeviceStatusForCreation(DeviceStatus deviceStatus)
    {
        // Generate identifier if not present (legacy behavior)
        if (string.IsNullOrEmpty(deviceStatus.Id))
        {
            deviceStatus.Id = GenerateIdentifier(deviceStatus);
        }

        // Ensure DeviceStatus has required properties for V3 compatibility
        if (string.IsNullOrEmpty(deviceStatus.CreatedAt))
        {
            deviceStatus.CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }
    }

    /// <summary>
    /// Generate identifier for device status following legacy API v3 logic
    /// Uses created_at and device fields for deduplication fallback
    /// </summary>
    /// <param name="deviceStatus">Device status record</param>
    /// <returns>Generated identifier</returns>
    private string GenerateIdentifier(DeviceStatus deviceStatus)
    {
        // Legacy API v3 uses created_at + device for devicestatus deduplication
        var identifierParts = new List<string>();

        if (!string.IsNullOrEmpty(deviceStatus.CreatedAt))
        {
            identifierParts.Add(deviceStatus.CreatedAt);
        }

        if (!string.IsNullOrEmpty(deviceStatus.Device))
        {
            identifierParts.Add(deviceStatus.Device);
        }

        // Add timestamp for uniqueness
        identifierParts.Add(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

        // If we have identifying parts, create a hash-based identifier
        if (identifierParts.Any())
        {
            var combined = string.Join("-", identifierParts);
            return $"devicestatus-{combined.GetHashCode():X}";
        }

        // Fallback to GUID for unique identification
        return Guid.CreateVersion7().ToString();
    }

    /// <summary>
    /// Parse create request from JsonElement for DeviceStatus objects
    /// </summary>
    /// <param name="jsonElement">JsonElement containing device status data (single object or array)</param>
    /// <param name="validationError">When validation fails, contains the error result to return</param>
    /// <returns>Collection of DeviceStatus objects</returns>
    private IEnumerable<DeviceStatus> ParseCreateRequestFromJsonElement(
        JsonElement jsonElement,
        out ActionResult? validationError
    )
    {
        var deviceStatusRecords = new List<DeviceStatus>();
        validationError = null;

        try
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in jsonElement.EnumerateArray())
                {
                    // Nightscout V3 Strict Validation: 'app' field is required in the JSON payload
                    if (!element.TryGetProperty("app", out _))
                    {
                        validationError = CreateV3ErrorResponse(400, "Bad or missing app field");
                        return Enumerable.Empty<DeviceStatus>();
                    }

                    var deviceStatus = JsonSerializer.Deserialize<DeviceStatus>(
                        element.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (deviceStatus != null)
                    {
                        deviceStatusRecords.Add(deviceStatus);
                    }
                }
            }
            else if (jsonElement.ValueKind == JsonValueKind.Object)
            {
                // Nightscout V3 Strict Validation: 'app' field is required
                if (!jsonElement.TryGetProperty("app", out _))
                {
                    validationError = CreateV3ErrorResponse(400, "Bad or missing app field");
                    return Enumerable.Empty<DeviceStatus>();
                }

                var deviceStatus = JsonSerializer.Deserialize<DeviceStatus>(
                    jsonElement.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
                if (deviceStatus != null)
                {
                    deviceStatusRecords.Add(deviceStatus);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse device status data from JsonElement");
            throw new ArgumentException("Invalid device status data format", ex);
        }

        return deviceStatusRecords;
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

    private object MapToV3Dto(DeviceStatus status)
    {
        // Build dictionary with only non-null optional fields to match Nightscout behavior
        var dto = new Dictionary<string, object?>
        {
            ["device"] = status.Device,
            ["created_at"] = status.CreatedAt,
            ["uploaderBattery"] = status.Uploader?.Battery,
            ["utcOffset"] = status.UtcOffset,
            ["identifier"] = status.Id,
            ["srvModified"] = status.Mills,
            ["srvCreated"] = status.Mills,
        };

        // Only include optional complex fields if they have values (Nightscout omits nulls)
        if (status.OpenAps != null)
            dto["openaps"] = status.OpenAps;
        if (status.Pump != null)
            dto["pump"] = status.Pump;
        if (status.Uploader != null)
            dto["uploader"] = status.Uploader;
        if (status.Loop != null)
            dto["loop"] = status.Loop;

        return dto;
    }
}
