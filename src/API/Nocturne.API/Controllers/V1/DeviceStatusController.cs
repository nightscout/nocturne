using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Legacy;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Device Status controller that provides 1:1 compatibility with Nightscout device status endpoints.
/// Implements the /api/v1/devicestatus/* endpoints from the legacy JavaScript implementation.
/// Writes decompose directly into V4 snapshot tables; reads project back from V4.
/// </summary>
/// <seealso cref="IDeviceStatusDecomposer"/>
/// <seealso cref="DeviceStatusProjectionService"/>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class DeviceStatusController : ControllerBase
{
    private readonly DeviceStatusProjectionService _projection;
    private readonly IDeviceStatusDecomposer _decomposer;
    private readonly IWriteSideEffects _sideEffects;
    private readonly IDataEventSink<DeviceStatus> _events;
    private readonly ILogger<DeviceStatusController> _logger;

    private const string CollectionName = "devicestatus";

    /// <summary>
    /// Suppress V4 decomposition in side effects — the controller decomposes directly.
    /// </summary>
    private static readonly WriteEffectOptions NoDecompose = new() { DecomposeToV4 = false };

    /// <summary>
    /// Initializes a new instance of <see cref="DeviceStatusController"/>.
    /// </summary>
    public DeviceStatusController(
        DeviceStatusProjectionService projection,
        IDeviceStatusDecomposer decomposer,
        IWriteSideEffects sideEffects,
        IDataEventSink<DeviceStatus> events,
        ILogger<DeviceStatusController> logger
    )
    {
        _projection = projection;
        _decomposer = decomposer;
        _sideEffects = sideEffects;
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Get device status entries with optional filtering and pagination
    /// </summary>
    /// <param name="count">Maximum number of device status entries to return (default: 10)</param>
    /// <param name="skip">Number of device status entries to skip for pagination (default: 0)</param>
    /// <param name="format">Output format: json (default), csv, tsv, or txt</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of device status entries ordered by most recent first</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v1/devicestatus")]
    [ProducesResponseType(typeof(DeviceStatus[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetDeviceStatus(
        [FromQuery] int count = 10,
        [FromQuery] int skip = 0,
        [FromQuery] string format = "json",
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Device status endpoint requested with count: {Count}, skip: {Skip} from {RemoteIpAddress}",
            count,
            skip,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Handle count parameter for Nightscout compatibility:
            // - 0 or negative: return empty array (Nightscout behavior)
            if (count <= 0)
            {
                _logger.LogDebug("Returning empty array for count={Count}", count);
                return Ok(Array.Empty<DeviceStatus>());
            }

            // Normalize skip parameter (negative is not valid)
            if (skip < 0)
            {
                skip = 0; // Normalize to 0 for Nightscout compatibility
            }

            // Extract find query parameters from query string
            string? findQuery = null;
            var queryKeys = HttpContext?.Request?.Query?.Keys;
            if (queryKeys != null)
            {
                var findParams = queryKeys
                    .Where(k => k.StartsWith("find["))
                    .ToList();

                if (findParams.Count > 0)
                {
                    var queryParts = findParams
                        .Select(k => $"{k}={HttpContext!.Request.Query[k]}")
                        .ToList();
                    findQuery = string.Join("&", queryParts);
                }
            }

            var deviceStatusEntries = await _projection.GetAsync(
                count, skip, findQuery, cancellationToken
            );
            var deviceStatusArray = deviceStatusEntries.ToArray();

            _logger.LogDebug(
                "Successfully returned {Count} device status entries",
                deviceStatusArray.Length
            );

            // Handle different output formats
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                return Ok(deviceStatusArray);
            }

            try
            {
                var formattedData = DataFormatService.FormatDeviceStatus(
                    deviceStatusArray,
                    format
                );
                var contentType = DataFormatService.GetContentType(format);
                return Content(formattedData, contentType);
            }
            catch (ArgumentException)
            {
                _logger.LogWarning("Invalid format requested: {Format}", format);
                return BadRequest(
                    $"Unsupported format: {format}. Supported formats are: json, csv, tsv, txt"
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching device status entries");
            return StatusCode(500, Array.Empty<DeviceStatus>());
        }
    }

    /// <summary>
    /// Create new device status entries
    /// </summary>
    /// <param name="body">Device status entry or array of entries to create (accepts both single object and array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created device status entries with assigned IDs</returns>
    [HttpPost]
    [Authorize]
    [NightscoutEndpoint("/api/v1/devicestatus")]
    [ProducesResponseType(typeof(DeviceStatus[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<DeviceStatus[]>> CreateDeviceStatus(
        [FromBody] JsonElement body,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Device status creation endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            DeviceStatus[] deviceStatusEntries;

            // Handle both single object and array of device status entries (Nightscout compatibility)
            if (body.ValueKind == JsonValueKind.Array)
            {
                deviceStatusEntries = JsonSerializer.Deserialize<DeviceStatus[]>(body.GetRawText())
                    ?? Array.Empty<DeviceStatus>();
            }
            else if (body.ValueKind == JsonValueKind.Object)
            {
                var singleEntry = JsonSerializer.Deserialize<DeviceStatus>(body.GetRawText());
                deviceStatusEntries = singleEntry != null
                    ? new[] { singleEntry }
                    : Array.Empty<DeviceStatus>();
            }
            else
            {
                _logger.LogWarning("Invalid JSON format for device status");
                return BadRequest("Invalid JSON format. Expected object or array of device status entries.");
            }

            if (deviceStatusEntries.Length == 0)
            {
                _logger.LogWarning("Device status creation requested with no entries");
                return BadRequest("At least one device status entry is required");
            }

            // Validate entries - device name is required (Relaxed for parity with Nightscout V1)
            for (int i = 0; i < deviceStatusEntries.Length; i++)
            {
                var deviceStatus = deviceStatusEntries[i];
                if (deviceStatus.Device == null)
                {
                    deviceStatus.Device = string.Empty;
                }
            }

            // Decompose each device status directly into V4 snapshot tables
            var projectedResults = new List<DeviceStatus>();
            foreach (var ds in deviceStatusEntries)
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

            var createdArray = projectedResults.ToArray();

            // Broadcast via WriteSideEffectsService (cache invalidation + SignalR)
            await _sideEffects.OnCreatedAsync(
                CollectionName,
                createdArray,
                NoDecompose,
                cancellationToken
            );

            await _events.OnCreatedAsync(createdArray.ToList(), cancellationToken);

            _logger.LogDebug(
                "Successfully created {Count} device status entries",
                createdArray.Length
            );

            // Nightscout returns 200 OK for POST, not 201 Created
            return Ok(createdArray);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in create device status request");
            return BadRequest("Invalid JSON format");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating device status entries");
            return StatusCode(500, Array.Empty<DeviceStatus>());
        }
    }

    /// <summary>
    /// Delete a device status entry by ID
    /// </summary>
    /// <param name="id">Device status ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    [HttpDelete("{id}")]
    [Authorize]
    [NightscoutEndpoint("/api/v1/devicestatus/:id")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteDeviceStatus(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Device status deletion endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Device status deletion requested with empty ID");
                return BadRequest("Device status ID is required");
            }

            // Get the projected record before deleting (for broadcast)
            var deviceStatusToDelete = await _projection.GetByIdAsync(id, cancellationToken);

            // Delete V4 snapshot records by legacy ID
            var deleted = await _decomposer.DeleteByLegacyIdAsync(id, cancellationToken);

            if (deleted > 0 || deviceStatusToDelete != null)
            {
                await _sideEffects.OnDeletedAsync(
                    CollectionName,
                    deviceStatusToDelete,
                    NoDecompose,
                    cancellationToken
                );

                await _events.OnDeletedAsync(deviceStatusToDelete, cancellationToken);

                _logger.LogDebug("Successfully deleted device status with ID: {Id}", id);
                return Ok();
            }
            else
            {
                _logger.LogDebug("Device status not found for deletion with ID: {Id}", id);
                return NotFound();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while deleting device status with ID: {Id}", id);
            return StatusCode(500);
        }
    }

    /// <summary>
    /// Bulk delete device status entries using query filters.
    /// Finds matching records via V4 projection, then cascade-deletes their V4 snapshots.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of deleted entries</returns>
    [HttpDelete]
    [Authorize]
    [NightscoutEndpoint("/api/v1/devicestatus")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> BulkDeleteDeviceStatus(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Device status bulk deletion endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Build find query from query string parameters
            var findQuery = HttpContext?.Request?.QueryString.Value ?? string.Empty;

            if (string.IsNullOrWhiteSpace(findQuery))
            {
                _logger.LogWarning(
                    "Bulk delete device status requested without query parameters - this would delete all entries!"
                );
                return BadRequest(
                    "Query parameters are required for bulk delete to prevent accidental deletion of all entries"
                );
            }

            // Remove the leading '?' if present
            if (findQuery.StartsWith("?"))
            {
                findQuery = findQuery.Substring(1);
            }

            // Find matching records via V4 projection, then delete each by legacy ID.
            // NOTE: hardcoded limit of 10,000 means bulk deletes silently truncate
            // if more records match. Nightscout legacy has the same limitation.
            const int bulkDeleteLimit = 10_000;
            var matchingRecords = (await _projection.GetAsync(
                count: bulkDeleteLimit, skip: 0, find: findQuery, ct: cancellationToken
            )).ToList();

            if (matchingRecords.Count == bulkDeleteLimit)
            {
                _logger.LogWarning(
                    "Bulk delete matched the maximum of {Limit} records — results may be truncated",
                    bulkDeleteLimit);
            }

            long deletedCount = 0;
            foreach (var record in matchingRecords)
            {
                if (!string.IsNullOrEmpty(record.Id))
                {
                    var count = await _decomposer.DeleteByLegacyIdAsync(record.Id, cancellationToken);
                    if (count > 0)
                        deletedCount++;
                }
            }

            await _sideEffects.OnBulkDeletedAsync(
                CollectionName,
                deletedCount,
                NoDecompose,
                cancellationToken
            );

            await _events.OnBulkDeletedAsync(deletedCount, cancellationToken);

            _logger.LogDebug(
                "Successfully bulk deleted {Count} device status entries",
                deletedCount
            );

            // Return response compatible with Nightscout format
            return Ok(new Dictionary<string, object>
            {
                ["result"] = new { n = deletedCount, ok = 1 },
                ["n"] = deletedCount,
                ["ok"] = 1
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during bulk delete of device status entries");
            return StatusCode(500);
        }
    }

    /// <summary>
    /// Alternative endpoint for device status - supports .json extension
    /// </summary>
    /// <param name="count">Maximum number of device status entries to return (default: 10)</param>
    /// <param name="skip">Number of device status entries to skip for pagination (default: 0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of device status entries ordered by most recent first</returns>
    [HttpGet("~/api/v1/devicestatus.json")]
    [NightscoutEndpoint("/api/v1/devicestatus.json")]
    [ProducesResponseType(typeof(DeviceStatus[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<DeviceStatus[]>> GetDeviceStatusJson(
        [FromQuery] int count = 10,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        // Delegate to the main endpoint
        return await GetDeviceStatus(count, skip, "json", cancellationToken);
    }
}
