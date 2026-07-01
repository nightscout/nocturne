using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.ClientDevices;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;

namespace Nocturne.API.Controllers.V4.ClientDevices;

/// <summary>
/// Manages registered client app installs (Prelude, the desktop Companion) that can be alert-engine
/// actuation targets. Distinct from <c>api/v4/devices</c> (the telemetry device catalog).
/// </summary>
/// <seealso cref="IClientDeviceService"/>
[ApiController]
[Tags("Client Devices")]
[Route("api/v4/client-devices")]
public class ClientDevicesController : ControllerBase
{
    private readonly IClientDeviceService _deviceService;
    private readonly ILogger<ClientDevicesController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ClientDevicesController"/> class.</summary>
    public ClientDevicesController(
        IClientDeviceService deviceService,
        ILogger<ClientDevicesController> logger)
    {
        _deviceService = deviceService;
        _logger = logger;
    }

    /// <summary>
    /// Register or refresh this device. Idempotent upsert keyed on the install id — apps call it on
    /// every startup to keep their advertised capabilities and last-seen time current.
    /// </summary>
    [HttpPost]
    [Authorize]
    [RequireScope(OAuthScopes.DeviceNotify, OAuthScopes.DeviceActuate)]
    [ProducesResponseType(typeof(ClientDeviceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ClientDeviceDto>> Register(
        [FromBody] RegisterDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        try
        {
            var device = await _deviceService.RegisterAsync(
                subjectId.Value,
                request,
                HttpContext.GetGrantedScopes(),
                cancellationToken);

            _logger.LogDebug(
                "Subject {SubjectId} registered device {DeviceId} (install {InstallId}).",
                subjectId, device.Id, device.InstallId);

            return Ok(device);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // The install id is already owned by another subject in this tenant.
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// The static device-action capability catalog (kinds + capabilities) that drives the rule
    /// builder's device-action authoring UI. Static metadata, so no device scope is required.
    /// </summary>
    [HttpGet("capabilities")]
    [RemoteQuery]
    [Authorize]
    [ProducesResponseType(typeof(DeviceCapabilityCatalog), StatusCodes.Status200OK)]
    public ActionResult<DeviceCapabilityCatalog> GetCapabilityCatalog()
        => Ok(DeviceCapabilities.BuildCatalog());

    /// <summary>Lists the devices registered by the current user, most-recently-seen first.</summary>
    [HttpGet]
    [RemoteQuery]
    [Authorize]
    [RequireScope(OAuthScopes.DeviceNotify, OAuthScopes.DeviceActuate)]
    [ProducesResponseType(typeof(List<ClientDeviceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<ClientDeviceDto>>> GetDevices(CancellationToken cancellationToken)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        var devices = await _deviceService.GetForSubjectAsync(subjectId.Value, cancellationToken);
        return Ok(devices.ToList());
    }

    /// <summary>Rename a device you own.</summary>
    [HttpPatch("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetDevices"])]
    [Authorize]
    [RequireScope(OAuthScopes.DeviceNotify, OAuthScopes.DeviceActuate)]
    [ProducesResponseType(typeof(ClientDeviceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClientDeviceDto>> Rename(
        Guid id,
        [FromBody] RenameDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        var updated = await _deviceService.RenameAsync(id, subjectId.Value, request.Label, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Revoke (remove) a device you own. Its future registrations start fresh.</summary>
    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetDevices"])]
    [Authorize]
    [RequireScope(OAuthScopes.DeviceNotify, OAuthScopes.DeviceActuate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        var removed = await _deviceService.DeleteAsync(id, subjectId.Value, cancellationToken);
        return removed ? NoContent() : NotFound();
    }

    /// <summary>
    /// The actuation intents currently active for this device — the reconcile snapshot a push-mode
    /// device reads on (re)connect to start anything still active and drop anything resolved. Empty
    /// for a local-engine device or one the caller does not own.
    /// </summary>
    [HttpGet("{id:guid}/active-intents")]
    [RemoteQuery]
    [Authorize]
    [RequireScope(OAuthScopes.DeviceNotify, OAuthScopes.DeviceActuate)]
    [ProducesResponseType(typeof(List<DeviceActionIntent>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<DeviceActionIntent>>> GetActiveIntents(
        Guid id,
        CancellationToken cancellationToken)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        var intents = await _deviceService.GetActiveIntentsAsync(id, subjectId.Value, cancellationToken);
        return Ok(intents.ToList());
    }
}
