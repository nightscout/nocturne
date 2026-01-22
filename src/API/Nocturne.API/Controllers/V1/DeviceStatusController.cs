using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Device Status controller that provides 1:1 compatibility with Nightscout device status endpoints
/// Implements the /api/v1/devicestatus/* endpoints from the legacy JavaScript implementation
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class DeviceStatusController : ControllerBase
{
    private readonly IDeviceStatusService _deviceStatusService;
    private readonly IDataFormatService _dataFormatService;
    private readonly ILogger<DeviceStatusController> _logger;

    public DeviceStatusController(
        IDeviceStatusService deviceStatusService,
        IDataFormatService dataFormatService,
        ILogger<DeviceStatusController> logger
    )
    {
        _deviceStatusService = deviceStatusService;
        _dataFormatService = dataFormatService;
        _logger = logger;
    }

    /// <summary>
    /// Get device status entries with optional filtering and pagination
    /// </summary>
    /// <param name="find">MongoDB-style find query filters (JSON format) - for unit tests</param>
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
        [FromQuery] string? find = null,
        [FromQuery] int count = 10,
        [FromQuery] int skip = 0,
        [FromQuery] string format = "json",
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
        // Use query string if it contains find parameters, otherwise use the find parameter for unit tests
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
            "Device status endpoint requested with count: {Count}, skip: {Skip}, findQuery: {FindQuery} from {RemoteIpAddress}",
            count,
            skip,
            findQuery,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Validate parameters
            if (count < 0)
            {
                _logger.LogWarning(
                    "Invalid count parameter: {Count}. Must be non-negative",
                    count
                );
                return BadRequest($"Count must be non-negative, got {count}");
            }

            if (skip < 0)
            {
                _logger.LogWarning("Invalid skip parameter: {Skip}. Must be >= 0", skip);
                return BadRequest($"Skip must be >= 0, got {skip}");
            }
            var deviceStatusEntries = await _deviceStatusService.GetDeviceStatusAsync(
                find: findQuery,
                count: count,
                skip: skip,
                cancellationToken: cancellationToken
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
                var formattedData = _dataFormatService.FormatDeviceStatus(
                    deviceStatusArray,
                    format
                );
                var contentType = _dataFormatService.GetContentType(format);
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
    /// <param name="deviceStatusEntries">Device status entries to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created device status entries with assigned IDs</returns>
    [HttpPost]
    [NightscoutEndpoint("/api/v1/devicestatus")]
    [ProducesResponseType(typeof(DeviceStatus[]), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<DeviceStatus[]>> CreateDeviceStatus(
        [FromBody] DeviceStatus[] deviceStatusEntries,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Device status creation endpoint requested with {Count} entries from {RemoteIpAddress}",
            deviceStatusEntries?.Length ?? 0,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (deviceStatusEntries == null || !deviceStatusEntries.Any())
            {
                _logger.LogWarning("Device status creation requested with no entries");
                return BadRequest("At least one device status entry is required");
            } // Validate entries
            for (int i = 0; i < deviceStatusEntries.Length; i++)
            {
                var deviceStatus = deviceStatusEntries[i];
                if (string.IsNullOrWhiteSpace(deviceStatus.Device))
                {
                    _logger.LogWarning(
                        "Device status entry at index {Index} has no device name",
                        i
                    );
                    return BadRequest($"Device status entry at index {i} must have a device name");
                }
            } // Process and create entries using domain service (which handles validation, processing, and WebSocket broadcasting)
            var createdEntries = await _deviceStatusService.CreateDeviceStatusAsync(
                deviceStatusEntries,
                cancellationToken
            );
            var createdArray = createdEntries.ToArray();

            _logger.LogDebug(
                "Successfully created {Count} device status entries",
                createdArray.Length
            );

            return CreatedAtAction(
                nameof(GetDeviceStatus),
                new { count = createdArray.Length },
                createdArray
            );
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

            var deleted = await _deviceStatusService.DeleteDeviceStatusAsync(id, cancellationToken);

            if (deleted)
            {
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
    /// Bulk delete device status entries using query filters
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of deleted entries</returns>
    [HttpDelete]
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
            var deletedCount = await _deviceStatusService.DeleteDeviceStatusAsync(
                findQuery,
                cancellationToken
            );

            _logger.LogDebug(
                "Successfully bulk deleted {Count} device status entries",
                deletedCount
            );

            // Return response compatible with Nightscout format
            return Ok(new { n = deletedCount });
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
    /// <param name="find">MongoDB-style find query filters (JSON format) - for unit tests</param>
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
        [FromQuery] string? find = null,
        [FromQuery] int count = 10,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        // Delegate to the main endpoint
        return await GetDeviceStatus(find, count, skip, "json", cancellationToken);
    }
}
