using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Controllers.Admin;

[ApiController]
[Route("api/v4/admin/tenants")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
[AllowDuringSetup]
public class TenantController : ControllerBase
{
    private readonly ITenantService _tenantService;
    private readonly ITenantRoleService _tenantRoleService;
    private readonly IMemberInviteService _memberInviteService;

    public TenantController(
        ITenantService tenantService,
        ITenantRoleService tenantRoleService,
        IMemberInviteService memberInviteService)
    {
        _tenantService = tenantService;
        _tenantRoleService = tenantRoleService;
        _memberInviteService = memberInviteService;
    }

    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<TenantDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
        => Ok(await _tenantService.GetAllAsync(ct));

    [HttpGet("{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        var tenant = await _tenantService.GetByIdAsync(id, ct);
        return tenant == null ? NotFound() : Ok(tenant);
    }

    [HttpPost]
    [RemoteCommand(Invalidates = ["GetAll"])]
    [ProducesResponseType(typeof(TenantCreatedDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTenantRequest request, CancellationToken ct)
    {
        var authContext = HttpContext.Items["AuthContext"] as AuthContext;
        var tenant = authContext?.SubjectId is { } creatorId
            ? await _tenantService.CreateAsync(request.Slug, request.DisplayName, creatorId, request.ApiSecret, ct)
            : await _tenantService.CreateWithoutOwnerAsync(request.Slug, request.DisplayName, request.ApiSecret, ct);
        return CreatedAtAction(nameof(GetById), new { id = tenant.Id }, tenant);
    }

    [HttpPut("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetAll", "GetById"])]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(
        Guid id, [FromBody] UpdateTenantRequest request, CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        var tenant = await _tenantService.UpdateAsync(id, request.DisplayName, request.IsActive, request.AllowAccessRequests, ct);
        return Ok(tenant);
    }

    [HttpPost("{id:guid}/members")]
    [RemoteCommand(Invalidates = ["GetById"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AddMember(
        Guid id, [FromBody] AddMemberRequest request, CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        await _tenantService.AddMemberAsync(id, request.SubjectId, request.RoleIds, request.DirectPermissions, ct: ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/members/{subjectId:guid}")]
    [RemoteCommand(Invalidates = ["GetById"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveMember(
        Guid id, Guid subjectId,
        [FromServices] NocturneDbContext dbContext,
        CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        var member = await dbContext.TenantMembers
            .Include(m => m.Subject)
            .Where(m => m.TenantId == id && m.SubjectId == subjectId)
            .FirstOrDefaultAsync(ct);

        if (member?.Subject?.IsSystemSubject == true)
            return Problem(detail: "Cannot remove system subject memberships", statusCode: 400, title: "Bad Request");

        await _tenantService.RemoveMemberAsync(id, subjectId, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/invites")]
    [RemoteCommand(Invalidates = ["GetById"])]
    [ProducesResponseType(typeof(MemberInviteResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateInvite(
        Guid id, [FromBody] CreateMemberInviteRequest request, CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        var authContext = HttpContext.Items["AuthContext"] as AuthContext;
        var result = await _memberInviteService.CreateInviteAsync(
            id,
            authContext!.SubjectId!.Value,
            request.RoleIds,
            request.DirectPermissions,
            request.Label,
            request.ExpiresInDays,
            request.MaxUses,
            request.LimitTo24Hours);

        return StatusCode(StatusCodes.Status201Created, result);
    }

    [HttpGet("{id:guid}/invites")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<MemberInviteInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListInvites(Guid id, CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        var invites = await _memberInviteService.GetInvitesForTenantAsync(id);
        return Ok(invites);
    }

    [HttpDelete("{id:guid}/invites/{inviteId:guid}")]
    [RemoteCommand(Invalidates = ["GetById", "ListInvites"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeInvite(Guid id, Guid inviteId, CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        var revoked = await _memberInviteService.RevokeInviteAsync(inviteId, id);
        return revoked ? NoContent() : NotFound();
    }

    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetAll"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _tenantService.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("provision")]
    [RemoteCommand(Invalidates = ["GetAll"])]
    [ProducesResponseType(typeof(ProvisionResult), StatusCodes.Status201Created)]
    public async Task<IActionResult> Provision(
        [FromBody] ProvisionRequest request, CancellationToken ct)
    {
        var result = await _tenantService.ProvisionWithOwnerAsync(
            request.Slug,
            request.DisplayName,
            request.OwnerUsername,
            request.OwnerEmail,
            request.Credential,
            ct);

        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>
    /// Verifies the authenticated caller is a member of the specified tenant
    /// with the Owner role (has superuser permission).
    /// </summary>
    private async Task<bool> IsCallerTenantOwnerAsync(Guid tenantId, CancellationToken ct)
    {
        var authContext = HttpContext.Items["AuthContext"] as AuthContext;
        if (authContext?.SubjectId is not { } subjectId)
            return false;

        var tenant = await _tenantService.GetByIdAsync(tenantId, ct);
        if (tenant == null) return false;

        var member = tenant.Members.FirstOrDefault(m => m.SubjectId == subjectId);
        if (member == null) return false;

        return member.Roles.Any(r => r.Slug == TenantPermissions.SeedRoles.Owner);
    }
}

public record CreateTenantRequest(string Slug, string DisplayName, string? ApiSecret = null);
public record UpdateTenantRequest(string DisplayName, bool IsActive, bool? AllowAccessRequests = null);
public record AddMemberRequest(Guid SubjectId, List<Guid> RoleIds, List<string>? DirectPermissions = null);

public record ProvisionRequest(
    string Slug,
    string DisplayName,
    string OwnerUsername,
    string OwnerEmail,
    ProvisionCredentialData Credential);

public class CreateMemberInviteRequest
{
    public List<Guid> RoleIds { get; set; } = [];
    public List<string>? DirectPermissions { get; set; }
    public string? Label { get; set; }
    public int ExpiresInDays { get; set; } = 7;
    public int? MaxUses { get; set; }
    public bool LimitTo24Hours { get; set; }
}
