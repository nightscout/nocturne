using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>
/// Tenant creation invites: links a platform admin hands out so that people can create an account
/// and a tenant of their own on this instance.
/// </summary>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/tenant-invites")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
public class TenantCreationInviteAdminController(ITenantCreationInviteService invites) : ControllerBase
{
    /// <inheritdoc cref="ITenantCreationInviteService.GetOverviewAsync"/>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantCreationInviteOverview), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOverview(CancellationToken ct) =>
        Ok(await invites.GetOverviewAsync(ct));

    /// <inheritdoc cref="ITenantCreationInviteService.CreateAsync"/>
    [HttpPost]
    [RemoteCommand(Invalidates = ["GetOverview"])]
    [ProducesResponseType(typeof(TenantCreationInviteCreated), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTenantCreationInviteRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Problem(detail: "Sign in as a person to make an invite link.", statusCode: 400, title: "Bad Request");

        var outcome = await invites.CreateAsync(subjectId, request, ct);
        return outcome.Refusal is { } refusal ? this.ToResult(refusal) : Ok(outcome.Value);
    }

    /// <inheritdoc cref="ITenantCreationInviteService.RevokeAsync"/>
    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetOverview"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) =>
        await invites.RevokeAsync(id, ct) ? NoContent() : NotFound();
}

/// <summary>Instance-wide policies a platform admin sets.</summary>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/platform-policy")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
public class PlatformPolicyController(IPlatformPolicyRepository policies) : ControllerBase
{
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(PlatformPolicy), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPolicy(CancellationToken ct) => Ok(await policies.GetAsync(ct));

    [HttpPut]
    [RemoteCommand(Invalidates = ["GetPolicy"])]
    [ProducesResponseType(typeof(PlatformPolicy), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetPolicy([FromBody] PlatformPolicy policy, CancellationToken ct)
    {
        await policies.SaveAsync(policy, ct);
        return Ok(policy);
    }
}
