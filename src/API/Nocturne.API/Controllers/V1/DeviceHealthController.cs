using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Controller for device health management operations
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/devices")]
public class DeviceHealthController : ControllerBase
{
    private readonly IDeviceRegistryService _deviceRegistryService;
    private readonly IDeviceHealthAnalysisService _healthAnalysisService;
    private readonly ILogger<DeviceHealthController> _logger;

    /// <summary>
    /// Initializes a new instance of the DeviceHealthController
    /// </summary>
    /// <param name="deviceRegistryService">Device registry service</param>
    /// <param name="healthAnalysisService">Health analysis service</param>
    /// <param name="logger">Logger</param>
    public DeviceHealthController(
        IDeviceRegistryService deviceRegistryService,
        IDeviceHealthAnalysisService healthAnalysisService,
        ILogger<DeviceHealthController> logger
    )
    {
        _deviceRegistryService = deviceRegistryService;
        _healthAnalysisService = healthAnalysisService;
        _logger = logger;
    }

    /// <summary>
    /// Get all devices for the current user
    /// </summary>
    /// <returns>List of user's devices</returns>
    [HttpGet]
    public async Task<ActionResult<List<DeviceHealth>>> GetUserDevices()
    {
        try
        {
            var userId = HttpContext.GetSubjectIdString()!;

            var devices = await _deviceRegistryService.GetUserDevicesAsync(
                userId,
                HttpContext.RequestAborted
            );
            return Ok(devices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user devices");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Register a new device
    /// </summary>
    /// <param name="request">Device registration request</param>
    /// <returns>Registered device information</returns>
    [HttpPost]
    public async Task<ActionResult<DeviceHealth>> RegisterDevice(
        [FromBody] DeviceRegistrationRequest request
    )
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var userId = HttpContext.GetSubjectIdString()!;

            var device = await _deviceRegistryService.RegisterDeviceAsync(
                userId,
                request,
                HttpContext.RequestAborted
            );

            _logger.LogInformation(
                "Device {DeviceId} registered successfully for user {UserId}",
                request.DeviceId,
                userId
            );

            return CreatedAtAction(nameof(GetDevice), new { id = device.DeviceId }, device);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid device registration request");
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Device registration failed");
            return Conflict(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering device");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get device health information
    /// </summary>
    /// <param name="id">Device identifier</param>
    /// <returns>Device health information</returns>
    [HttpGet("{id}/health")]
    public async Task<ActionResult<DeviceHealthAnalysis>> GetDeviceHealth([Required] string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("Device ID is required");
            }

            var device = await _deviceRegistryService.GetDeviceAsync(
                id,
                HttpContext.RequestAborted
            );
            if (device == null)
            {
                return NotFound($"Device {id} not found");
            }

            var userId = HttpContext.GetSubjectIdString();
            if (device.UserId != userId && !HttpContext.IsAdmin())
            {
                return Forbid();
            }

            var healthAnalysis = await _healthAnalysisService.AnalyzeDeviceHealthAsync(
                id,
                HttpContext.RequestAborted
            );
            return Ok(healthAnalysis);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting device health for device {DeviceId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get device information
    /// </summary>
    /// <param name="id">Device identifier</param>
    /// <returns>Device information</returns>
    [HttpGet("{id}")]
    public async Task<ActionResult<DeviceHealth>> GetDevice([Required] string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("Device ID is required");
            }

            var device = await _deviceRegistryService.GetDeviceAsync(
                id,
                HttpContext.RequestAborted
            );
            if (device == null)
            {
                return NotFound($"Device {id} not found");
            }

            var userId = HttpContext.GetSubjectIdString();
            if (device.UserId != userId && !HttpContext.IsAdmin())
            {
                return Forbid();
            }

            return Ok(device);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting device {DeviceId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Update device settings
    /// </summary>
    /// <param name="id">Device identifier</param>
    /// <param name="settings">Device settings update</param>
    /// <returns>Success response</returns>
    [HttpPut("{id}/settings")]
    public async Task<ActionResult> UpdateDeviceSettings(
        [Required] string id,
        [FromBody] DeviceSettingsUpdate settings
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("Device ID is required");
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var device = await _deviceRegistryService.GetDeviceAsync(
                id,
                HttpContext.RequestAborted
            );
            if (device == null)
            {
                return NotFound($"Device {id} not found");
            }

            var userId = HttpContext.GetSubjectIdString();
            if (device.UserId != userId && !HttpContext.IsAdmin())
            {
                return Forbid();
            }

            await _deviceRegistryService.UpdateDeviceSettingsAsync(
                id,
                settings,
                HttpContext.RequestAborted
            );

            _logger.LogInformation("Device settings updated for device {DeviceId}", id);

            return NoContent();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid device settings update request");
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Device settings update failed");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating device settings for device {DeviceId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Update device health metrics
    /// </summary>
    /// <param name="id">Device identifier</param>
    /// <param name="update">Device health update</param>
    /// <returns>Success response</returns>
    [HttpPut("{id}/health")]
    public async Task<ActionResult> UpdateDeviceHealth(
        [Required] string id,
        [FromBody] DeviceHealthUpdate update
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("Device ID is required");
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var device = await _deviceRegistryService.GetDeviceAsync(
                id,
                HttpContext.RequestAborted
            );
            if (device == null)
            {
                return NotFound($"Device {id} not found");
            }

            var userId = HttpContext.GetSubjectIdString();
            if (device.UserId != userId && !HttpContext.IsAdmin())
            {
                return Forbid();
            }

            await _deviceRegistryService.UpdateDeviceHealthAsync(
                id,
                update,
                HttpContext.RequestAborted
            );

            _logger.LogInformation("Device health updated for device {DeviceId}", id);

            return NoContent();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid device health update request");
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Device health update failed");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating device health for device {DeviceId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Remove a device from the registry
    /// </summary>
    /// <param name="id">Device identifier</param>
    /// <returns>Success response</returns>
    [HttpDelete("{id}")]
    public async Task<ActionResult> RemoveDevice([Required] string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("Device ID is required");
            }

            var device = await _deviceRegistryService.GetDeviceAsync(
                id,
                HttpContext.RequestAborted
            );
            if (device == null)
            {
                return NotFound($"Device {id} not found");
            }

            var userId = HttpContext.GetSubjectIdString();
            if (device.UserId != userId && !HttpContext.IsAdmin())
            {
                return Forbid();
            }

            await _deviceRegistryService.RemoveDeviceAsync(id, HttpContext.RequestAborted);

            _logger.LogInformation("Device {DeviceId} removed successfully", id);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Device removal failed");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing device {DeviceId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get device health report
    /// </summary>
    /// <param name="id">Device identifier</param>
    /// <param name="period">Report period in days (default: 30)</param>
    /// <returns>Device health report</returns>
    [HttpGet("{id}/report")]
    public async Task<ActionResult<DeviceHealthReport>> GetDeviceHealthReport(
        [Required] string id,
        [FromQuery] int period = 30
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("Device ID is required");
            }

            if (period <= 0 || period > 365)
            {
                return BadRequest("Period must be between 1 and 365 days");
            }

            var device = await _deviceRegistryService.GetDeviceAsync(
                id,
                HttpContext.RequestAborted
            );
            if (device == null)
            {
                return NotFound($"Device {id} not found");
            }

            var userId = HttpContext.GetSubjectIdString();
            if (device.UserId != userId && !HttpContext.IsAdmin())
            {
                return Forbid();
            }

            var report = await _healthAnalysisService.GenerateHealthReportAsync(
                id,
                period,
                HttpContext.RequestAborted
            );
            return Ok(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating health report for device {DeviceId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get maintenance predictions for a device
    /// </summary>
    /// <param name="id">Device identifier</param>
    /// <returns>Maintenance prediction</returns>
    [HttpGet("{id}/maintenance/prediction")]
    public async Task<ActionResult<MaintenancePrediction>> GetMaintenancePrediction(
        [Required] string id
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("Device ID is required");
            }

            var device = await _deviceRegistryService.GetDeviceAsync(
                id,
                HttpContext.RequestAborted
            );
            if (device == null)
            {
                return NotFound($"Device {id} not found");
            }

            var userId = HttpContext.GetSubjectIdString();
            if (device.UserId != userId && !HttpContext.IsAdmin())
            {
                return Forbid();
            }

            var prediction = await _healthAnalysisService.PredictMaintenanceNeedsAsync(
                id,
                HttpContext.RequestAborted
            );
            return Ok(prediction);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting maintenance prediction for device {DeviceId}", id);
            return StatusCode(500, "Internal server error");
        }
    }
}
