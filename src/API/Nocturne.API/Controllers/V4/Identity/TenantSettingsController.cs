using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Tenant-level settings a tenant's own administrators control, as opposed to the platform-admin
/// surface in <c>PlatformAdmin.TenantController</c>.
/// </summary>
[ApiController]
[Tags("Identity")]
[Route("api/v4/tenant-settings")]
[Produces("application/json")]
[Authorize]
public class TenantSettingsController : ControllerBase
{
    private readonly ITenantService _tenantService;
    private readonly ITenantAccessor _tenantAccessor;

    public TenantSettingsController(ITenantService tenantService, ITenantAccessor tenantAccessor)
    {
        _tenantService = tenantService;
        _tenantAccessor = tenantAccessor;
    }

    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TenantSettingsDto>> GetTenantSettings(CancellationToken ct)
    {
        if (!HasPermission(Scope.TenantSettings))
            return Forbid();

        return Ok(await _tenantService.GetSettingsAsync(_tenantAccessor.TenantId, ct));
    }

    // The demo's account is shared and obtainable without signing up, and it holds
    // tenant.settings — so a permission gate alone would let any visitor take the demo's own
    // reference down for everyone until the next reset.
    [DenyDemoSubject]
    [HttpPut("public-docs")]
    [RemoteCommand(Invalidates = ["GetTenantSettings"])]
    [ProducesResponseType(typeof(TenantSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TenantSettingsDto>> SetPublicDocs(
        [FromBody] SetPublicDocsRequest request, CancellationToken ct)
    {
        if (!HasPermission(Scope.TenantSettings))
            return Forbid();

        return Ok(await _tenantService.SetAllowPublicDocsAsync(
            _tenantAccessor.TenantId, request.Enabled, ct));
    }

    private bool HasPermission(string permission)
        => Scope.Satisfies(HttpContext.GetGrantedScopes(), permission);
}

public record SetPublicDocsRequest(bool Enabled);
