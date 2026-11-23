using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// The devices the signed-in account trusted at the authenticator step. A sign-in from a trusted
/// device skips the authenticator code until the trust expires. A caller can list the devices,
/// stop trusting one device and stop trusting all devices.
/// </summary>
/// <remarks>
/// A device becomes trusted only at <c>POST /api/auth/totp/login</c>, with a verified code. Nothing
/// here can add one.
/// </remarks>
/// <seealso cref="ITrustedDeviceService"/>
[ApiController]
[Route("api/auth/trusted-devices")]
[Tags("Authentication")]
[Authorize]
[RequireInteractiveSession]
[ErrorEnvelope]
[Produces("application/json")]
public class TrustedDevicesController : ControllerBase
{
    private readonly ITrustedDeviceService _trustedDevices;
    private readonly IAuthAuditService _auditService;
    private readonly OidcOptions _options;

    /// <summary>
    /// Creates a new instance of TrustedDevicesController.
    /// </summary>
    public TrustedDevicesController(
        ITrustedDeviceService trustedDevices,
        IAuthAuditService auditService,
        IOptions<OidcOptions> options)
    {
        _trustedDevices = trustedDevices;
        _auditService = auditService;
        _options = options.Value;
    }

    /// <summary>
    /// List the devices the authenticated user trusted, most recently trusted first. Expired
    /// devices are not listed.
    /// </summary>
    [HttpGet]
    [DenyDemoSubject]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<TrustedDeviceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<TrustedDeviceDto>>> List(CancellationToken ct)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        var devices = await _trustedDevices.ListAsync(subjectId.Value, CurrentToken, ct);

        return Ok(devices
            .Select(d => new TrustedDeviceDto
            {
                Id = d.Id,
                DeviceDescription = d.DeviceDescription,
                CreatedAt = d.CreatedAt,
                LastUsedAt = d.LastUsedAt,
                ExpiresAt = d.ExpiresAt,
                IsCurrent = d.IsCurrent,
            })
            .ToList());
    }

    /// <summary>
    /// Stop trusting one device. Its next sign-in needs the authenticator code. Sessions stay
    /// signed in.
    /// </summary>
    /// <param name="id">The trusted device to remove.</param>
    /// <response code="204">The device is no longer trusted.</response>
    /// <response code="404">The caller has no such trusted device.</response>
    [HttpDelete("{id:guid}")]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["List"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        var devices = await _trustedDevices.ListAsync(subjectId.Value, CurrentToken, ct);
        var wasCurrent = devices.Any(d => d.Id == id && d.IsCurrent);

        // Subject-scoped, so another subject's device id matches nothing and reports 404.
        if (!await _trustedDevices.RevokeAsync(id, subjectId.Value, ct))
        {
            return NotFound();
        }

        if (wasCurrent)
        {
            Response.ClearTrustedDeviceCookie(_options);
        }

        await LogRevokedAsync(subjectId.Value, new { deviceId = id });

        return NoContent();
    }

    /// <summary>
    /// Stop trusting every device. The next sign-in on each one needs the authenticator code.
    /// Sessions stay signed in.
    /// </summary>
    [HttpPost("revoke-all")]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["List"])]
    [ProducesResponseType(typeof(RevokeTrustedDevicesResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RevokeTrustedDevicesResultDto>> RevokeAll(CancellationToken ct)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        var revoked = await _trustedDevices.RevokeAllAsync(subjectId.Value, ct);
        Response.ClearTrustedDeviceCookie(_options);

        if (revoked > 0)
        {
            await LogRevokedAsync(subjectId.Value, new { all = true, count = revoked });
        }

        return Ok(new RevokeTrustedDevicesResultDto { RevokedCount = revoked });
    }

    private string? CurrentToken => Request.Cookies[SessionCookieExtensions.TrustedDeviceCookieName];

    private Task LogRevokedAsync(Guid subjectId, object details) =>
        _auditService.LogAsync(AuthAuditEventType.DeviceTrustRevoked, subjectId, success: true,
            ipAddress: HttpContext.GetClientAddress(),
            userAgent: Request.Headers.UserAgent.ToString(),
            detailsJson: JsonSerializer.Serialize(details));
}

/// <summary>
/// A trusted device, as returned by GET /api/auth/trusted-devices.
/// </summary>
public class TrustedDeviceDto
{
    public Guid Id { get; set; }

    /// <summary>Browser and platform read from the device's user agent when it was trusted.</summary>
    public string? DeviceDescription { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When the device last signed in without a code, or null if it has not yet.</summary>
    public DateTime? LastUsedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Whether this row is the device making the request.</summary>
    public bool IsCurrent { get; set; }
}

/// <summary>
/// Result of POST /api/auth/trusted-devices/revoke-all.
/// </summary>
public class RevokeTrustedDevicesResultDto
{
    public int RevokedCount { get; set; }
}
