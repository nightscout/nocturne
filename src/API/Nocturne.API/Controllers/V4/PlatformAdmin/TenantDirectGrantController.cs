using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>
/// Platform-admin controller for managing a tenant's direct grant tokens.
/// </summary>
/// <remarks>
/// Lets a platform administrator (including instance-key callers, which have no subject of their
/// own) mint, list, and revoke direct grants on behalf of a tenant member — for example an
/// external provisioner issuing a tenant-scoped API token at provision time. The grant is issued
/// to an explicit subject, who must be a member of the tenant. Token generation and storage are
/// shared with the self-service <see cref="DirectGrantController"/> via
/// <see cref="IDirectGrantService"/>.
/// </remarks>
/// <seealso cref="IDirectGrantService"/>
/// <seealso cref="DirectGrantController"/>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/tenants/{tenantId:guid}/direct-grants")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
[AllowDuringSetup]
public class TenantDirectGrantController : ControllerBase
{
    private readonly IDbContextFactory<NocturneDbContext> _dbContextFactory;
    private readonly ITenantMemberService _tenantMemberService;
    private readonly IDirectGrantService _directGrantService;

    public TenantDirectGrantController(
        IDbContextFactory<NocturneDbContext> dbContextFactory,
        ITenantMemberService tenantMemberService,
        IDirectGrantService directGrantService)
    {
        _dbContextFactory = dbContextFactory;
        _tenantMemberService = tenantMemberService;
        _directGrantService = directGrantService;
    }

    /// <summary>
    /// Create a direct grant for a member of the tenant. The plaintext token is returned once
    /// and cannot be retrieved again.
    /// </summary>
    /// <param name="tenantId">The tenant the grant is bound to.</param>
    /// <param name="request">The subject to issue the grant to, plus label and scopes.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="CreateDirectGrantResponse"/> containing the grant ID and the single-use plaintext token.</returns>
    [HttpPost]
    [RemoteCommand(Invalidates = ["List"])]
    [ProducesResponseType(typeof(CreateDirectGrantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateDirectGrantResponse>> Create(
        Guid tenantId, [FromBody] AdminCreateDirectGrantRequest request, CancellationToken ct)
    {
        if (!await _tenantMemberService.IsMemberAsync(request.SubjectId, tenantId, ct))
        {
            return Problem(
                detail: "Subject is not a member of this tenant", statusCode: 400, title: "Bad Request");
        }

        await using var dbContext = await _dbContextFactory.CreateTenantPinnedContextAsync(tenantId, ct);

        var result = await _directGrantService.CreateAsync(
            dbContext, request.SubjectId, request.Label, request.Scopes,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            actor: CallerDescription(),
            ct: ct);

        if (result.Error != null)
        {
            return Problem(detail: result.Error, statusCode: 400, title: "Bad Request");
        }

        return Ok(result.Response);
    }

    /// <summary>
    /// List all active direct grants on the tenant, across all its members.
    /// Never returns the token itself.
    /// </summary>
    /// <param name="tenantId">The tenant whose grants to list.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The tenant's non-revoked direct grants, newest first.</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<DirectGrantDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<DirectGrantDto>>> List(Guid tenantId, CancellationToken ct)
    {
        await using var dbContext = await _dbContextFactory.CreateTenantPinnedContextAsync(tenantId, ct);

        var grants = await _directGrantService.ListAsync(dbContext, subjectId: null, ct);
        return Ok(grants);
    }

    /// <summary>
    /// Revoke a direct grant on the tenant. This operation is idempotent.
    /// </summary>
    /// <param name="tenantId">The tenant the grant belongs to.</param>
    /// <param name="grantId">The GUID of the grant to revoke.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><c>204 No Content</c> on success (including when already revoked); <c>404 Not Found</c> if the grant does not exist on the tenant.</returns>
    [HttpDelete("{grantId:guid}")]
    [RemoteCommand(Invalidates = ["List"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid tenantId, Guid grantId, CancellationToken ct)
    {
        await using var dbContext = await _dbContextFactory.CreateTenantPinnedContextAsync(tenantId, ct);

        var found = await _directGrantService.RevokeAsync(
            dbContext, grantId, subjectId: null,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            actor: CallerDescription(),
            ct: ct);

        if (!found)
        {
            return Problem(detail: "Direct grant not found", statusCode: 404, title: "Not Found");
        }

        return NoContent();
    }

    /// <summary>
    /// Describes the platform-admin caller for the audit trail: their subject ID, or the auth
    /// type (e.g. <c>InstanceKey</c>) for callers with no subject of their own.
    /// </summary>
    private string CallerDescription()
    {
        var auth = HttpContext.GetAuthContext();
        return auth?.SubjectId?.ToString() ?? auth?.AuthType.ToString() ?? "unknown";
    }
}

/// <summary>
/// Request to create a direct grant for an explicit subject on a tenant.
/// </summary>
public class AdminCreateDirectGrantRequest : CreateDirectGrantRequest
{
    /// <summary>The tenant member the grant is issued to.</summary>
    public Guid SubjectId { get; set; }
}
