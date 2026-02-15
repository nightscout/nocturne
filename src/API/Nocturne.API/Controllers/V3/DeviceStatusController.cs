using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// V3 DeviceStatus controller that provides full V3 API compatibility with Nightscout devicestatus endpoints
/// Implements the /api/v3/devicestatus endpoints with pagination, field selection, sorting, and advanced filtering
/// </summary>
[ApiController]
[Route("api/v3/[controller]")]
public class DeviceStatusController : BaseV3Controller<DeviceStatus>
{
    private readonly IDeviceStatusService _deviceStatusService;

    public DeviceStatusController(
        IDeviceStatusService deviceStatusService,
        IPostgreSqlService postgreSqlService,
        IDataFormatService dataFormatService,
        IDocumentProcessingService documentProcessingService,
        ILogger<DeviceStatusController> logger
    )
        : base(postgreSqlService, dataFormatService, documentProcessingService, logger)
    {
        _deviceStatusService = deviceStatusService;
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

            // Get device status using existing backend with V3 parameters
            var deviceStatusListRaw =
                await _postgreSqlService.GetDeviceStatusWithAdvancedFilterAsync(
                    parameters.Limit,
                    parameters.Offset,
                    findQuery,
                    reverseResults,
                    cancellationToken
                );

            var deviceStatusList = deviceStatusListRaw.ToList();

            // Get total count for pagination
            var totalCount = await GetTotalCountAsync(null, findQuery, cancellationToken);

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
            var result = await _deviceStatusService.GetDeviceStatusByIdAsync(id, cancellationToken);

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
                    var existing = await _postgreSqlService.GetDeviceStatusByIdAsync(
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

            // Create device status records with deduplication support
            var created = await _deviceStatusService.CreateDeviceStatusAsync(
                recordsList,
                cancellationToken
            );

            return CreatedAtAction(
                nameof(GetDeviceStatusById),
                new { id = created.First().Id },
                new { status = 200, result = created.Select(MapToV3Dto) }
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
    /// Update a device status record by ID with V3 format
    /// </summary>
    /// <param name="id">Device status ID to update</param>
    /// <param name="request">Updated device status data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated device status record</returns>
    [HttpPut("{id}")]
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

        // Ensure Device maps correctly (from app or device alias)
        if (string.IsNullOrWhiteSpace(deviceStatus.Device))
        {
            // If app was present but deserialization failed to map to Device?
            // Should not happen if DeviceStatus model maps it.
            // But if specific mapping needed, handle here.
        }

        deviceStatus.Id = id;
        ProcessDeviceStatusForCreation(deviceStatus);

        try
        {
            var updated = await _deviceStatusService.UpdateDeviceStatusAsync(
                id,
                deviceStatus,
                cancellationToken
            );

            if (updated == null)
            {
                return CreateV3ErrorResponse(404, "Device status not found");
            }

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
            var deleted = await _postgreSqlService.DeleteDeviceStatusAsync(id, cancellationToken);

            if (!deleted)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Device status not found",
                    $"Device status with ID '{id}' was not found"
                );
            }

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
    /// Get device status records modified since a given timestamp (for AAPS incremental sync)
    /// </summary>
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

            var deviceStatuses = await _postgreSqlService.GetDeviceStatusModifiedSinceAsync(
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

    /// <summary>
    /// Get total count for pagination support
    /// </summary>
    private async Task<long> GetTotalCountAsync(
        string? type,
        string? findQuery,
        CancellationToken cancellationToken,
        string collection = "devicestatus"
    )
    {
        try
        {
            return await _postgreSqlService.CountDeviceStatusAsync(findQuery, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not get total count for {Collection}, using approximation",
                collection
            );
            return 0; // Return 0 for count errors to maintain API functionality
        }
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
