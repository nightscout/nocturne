using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Configuration;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Helpers;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Services.Legacy;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Nightscout device status documents. A client can list, create and delete them.
/// </summary>
/// <remarks>
/// Implements the /api/v1/devicestatus/* endpoints from the legacy JavaScript implementation.
/// </remarks>
/// <seealso cref="IDeviceStatusService"/>
[ApiController]
[Tags("V1")]
[Route("api/v1/[controller]")]
[Authorize(Policy = PolicyNames.HasPermissions)]
[ErrorEnvelope]
public class DeviceStatusController : ControllerBase
{
    private readonly IDeviceStatusService _deviceStatuses;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeviceStatusController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DeviceStatusController"/>.
    /// </summary>
    public DeviceStatusController(
        IDeviceStatusService deviceStatuses,
        TimeProvider timeProvider,
        ILogger<DeviceStatusController> logger
    )
    {
        _deviceStatuses = deviceStatuses;
        _timeProvider = timeProvider;
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
    [RequireScope(Scope.DevicesRead)]
    public async Task<ActionResult> GetDeviceStatus(
        [FromQuery] int? count = null,
        [FromQuery] int skip = 0,
        [FromQuery] string format = "json",
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Device status endpoint requested with count: {Count}, skip: {Skip} from {RemoteIpAddress}",
            count,
            skip,
            HttpContext?.GetClientAddress() ?? "unknown"
        );

        var pageSize = LegacyReadLimits.ZeroAsAbsent(count) ?? 10;
        if (pageSize < 0)
        {
            _logger.LogDebug("Returning empty array for count={Count}", count);
            return Ok(Array.Empty<DeviceStatus>());
        }

        // Normalize skip parameter (negative is not valid)
        if (skip < 0)
        {
            skip = 0; // Normalize to 0 for Nightscout compatibility
        }

        var find = FindQuery.ParseDeviceStatus(LegacyFindDateWindow.Apply(
            LegacyFindQueryString.Resolve(HttpContext?.Request, boundFind: null),
            _timeProvider.GetUtcNow()
        ));
        var deviceStatusEntries = await _deviceStatuses.GetDeviceStatusesAsync(
            find, LegacyReadLimits.ClampMergedCount(pageSize), skip, cancellationToken
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

    /// <summary>
    /// Create new device status entries
    /// </summary>
    /// <param name="body">Device status entry or array of entries to create (accepts both single object and array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created device status entries with assigned IDs</returns>
    [HttpPost]
    [Authorize]
    [RequireScope(Scope.DevicesReadWrite)]
    [NightscoutEndpoint("/api/v1/devicestatus")]
    [ProducesResponseType(typeof(DeviceStatus[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<DeviceStatus[]>> CreateDeviceStatus(
        [FromBody] JsonElement body,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var deviceStatusEntries = NightscoutJsonBody.ReadObjects<DeviceStatus>(body);
            if (deviceStatusEntries is null)
            {
                _logger.LogWarning("Invalid JSON format for device status");
                return BadRequest("Invalid JSON format. Expected object or array of device status entries.");
            }

            if (deviceStatusEntries.Count == 0)
            {
                _logger.LogWarning("Device status creation requested with no entries");
                return BadRequest("At least one device status entry is required");
            }

            // Validate entries - device name is required (Relaxed for parity with Nightscout V1)
            for (int i = 0; i < deviceStatusEntries.Count; i++)
            {
                var deviceStatus = deviceStatusEntries[i];
                if (deviceStatus.Device == null)
                {
                    deviceStatus.Device = string.Empty;
                }
                if (string.IsNullOrEmpty(deviceStatus.Id))
                {
                    deviceStatus.Id = MongoObjectId.NewObjectId();
                }
            }

            var createdArray = (await _deviceStatuses.CreateDeviceStatusesAsync(
                deviceStatusEntries, cancellationToken: cancellationToken)).ToArray();

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
    }

    /// <summary>
    /// Delete a device status entry by ID
    /// </summary>
    /// <param name="id">Device status ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    [HttpDelete("{id}")]
    [Authorize]
    [RequireScope(Scope.DevicesReadWrite)]
    [NightscoutEndpoint("/api/v1/devicestatus/:id")]
    [ScopeDecidedBy(typeof(LegacyDeleteStatus), nameof(LegacyDeleteStatus.AnyId))]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteDeviceStatus(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Device status deletion endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.GetClientAddress() ?? "unknown"
        );

        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("Device status deletion requested with empty ID");
            return BadRequest("Device status ID is required");
        }

        if (id == LegacyDeleteStatus.AnyId)
        {
            return HttpContext?.HasScope(Scope.FullAccess) == true
                ? await BulkDeleteDeviceStatus(cancellationToken)
                : Forbid();
        }

        if (await _deviceStatuses.DeleteDeviceStatusAsync(id, cancellationToken))
        {
            _logger.LogDebug("Successfully deleted device status with ID: {Id}", id);
            return Ok(LegacyDeleteStatus.For(1));
        }

        _logger.LogDebug("Device status not found for deletion with ID: {Id}", id);
        return Ok(LegacyDeleteStatus.For(0));
    }

    /// <summary>
    /// Bulk delete device status entries using query filters.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of deleted entries</returns>
    [HttpDelete]
    [Authorize]
    [RequireScope(Scope.FullAccess)]
    [NightscoutEndpoint("/api/v1/devicestatus")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> BulkDeleteDeviceStatus(
        CancellationToken cancellationToken = default
    )
    {
        var findQuery = LegacyFindQueryString.Resolve(HttpContext?.Request, boundFind: null);
        var find = FindQuery.ParseDeviceStatus(findQuery);

        if (find.IsEmpty || find.HasInexactCondition)
        {
            _logger.LogWarning("Bulk delete device status refused: the query names no find it can apply");
            return BadRequest(
                "A find is required for bulk delete, and every condition in it must be one it can evaluate"
            );
        }

        // Nightscout windows every delete naming no created_at or _id, a time-only one included.
        var deletedCount = await _deviceStatuses.DeleteDeviceStatusesAsync(
            FindQuery.ParseDeviceStatus(
                LegacyFindDateWindow.Apply(findQuery, _timeProvider.GetUtcNow(), windowUnfiltered: true)),
            cancellationToken
        );

        _logger.LogDebug(
            "Successfully bulk deleted {Count} device status entries",
            deletedCount
        );

        return Ok(LegacyDeleteStatus.For(deletedCount));
    }
}
