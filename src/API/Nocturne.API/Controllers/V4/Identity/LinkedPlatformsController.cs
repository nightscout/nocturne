using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Authorization;
using OpenApi.Remote.Attributes;
using Nocturne.API.Services.Chat;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.API.Attributes;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// The chat platforms, such as Discord or Telegram, with at least one linked account in the tenant.
/// A caller can read this list to see which platforms are connected.
/// </summary>
/// <remarks>
/// This endpoint is a thin read-only view over the <see cref="ChatIdentityDirectoryService"/> that
/// reports which bot platforms have at least one registered identity within the resolved tenant,
/// allowing the frontend to display "connected platform" status without requiring a per-platform API call.
/// </remarks>
/// <seealso cref="ChatIdentityDirectoryService"/>
/// <seealso cref="ChatIdentityController"/>
[ApiController]
[Tags("Identity")]
[Authorize]
[Route("api/v4/chat-identity")]
[DenyNoAccountViewer]
public class LinkedPlatformsController(
    ChatIdentityDirectoryService directory,
    ITenantAccessor tenantAccessor) : ControllerBase
{
    [HttpGet("linked-platforms")]
    [RequireScope(Scope.AlertsRead, Scope.TenantSettings)]
    [RemoteQuery]
    [ProducesResponseType(typeof(LinkedPlatformsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LinkedPlatformsResponse>> GetLinkedPlatforms(
        CancellationToken ct)
    {
        var entries = await directory.GetByTenantAsync(tenantAccessor.TenantId, ct);
        var platforms = entries.Select(e => e.Platform).Distinct().ToList();
        return Ok(new LinkedPlatformsResponse { Platforms = platforms });
    }
}

public class LinkedPlatformsResponse
{
    public IReadOnlyList<string> Platforms { get; set; } = [];
}
