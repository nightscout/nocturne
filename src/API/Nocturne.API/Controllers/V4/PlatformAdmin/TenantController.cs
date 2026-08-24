using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>
/// Platform-admin controller for managing tenants on the instance.
/// </summary>
/// <remarks>
/// In multi-tenant deployments, platform administrators can create new tenants, update tenant
/// settings (display name, access-request flag), and deactivate tenants. Restricted to users
/// with the <c>platform_admin</c> role.
/// </remarks>
/// <seealso cref="ITenantService"/>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/tenants")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
[AllowDuringSetup]
public class TenantController : ControllerBase
{
    private readonly ITenantService _tenantService;
    private readonly ITenantRoleService _tenantRoleService;

    public TenantController(
        ITenantService tenantService,
        ITenantRoleService tenantRoleService)
    {
        _tenantService = tenantService;
        _tenantRoleService = tenantRoleService;
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
            ? await _tenantService.CreateAsync(request.Slug, request.DisplayName, creatorId, ct)
            : await _tenantService.CreateWithoutOwnerAsync(request.Slug, request.DisplayName, ct);
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

    /// <inheritdoc cref="ITenantService.AddMemberAsync"/>
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

    /// <inheritdoc cref="ITenantService.RemoveMemberAsync"/>
    [HttpDelete("{id:guid}/members/{subjectId:guid}")]
    [RemoteCommand(Invalidates = ["GetById"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid subjectId, CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        var result = await _tenantService.RemoveMemberAsync(id, subjectId, ct);
        return result.Ok
            ? NoContent()
            : Problem(
                detail: result.ErrorDescription, statusCode: 400, title: result.ErrorDescription);
    }

    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetAll"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await _tenantService.DeleteAsync(id, ct);
        }
        catch (KeyNotFoundException)
        {
            // The 404 this action already advertises. ITenantService.DeleteAsync throws for an
            // unknown id, and without this the caller gets a 500 — which reads as "the platform
            // is broken", not "there is nothing here to delete". Callers that delete a tenant as
            // part of a larger teardown (nocturne-cloud's retention purge) treat 404 as success
            // so a partially-completed run can be retried; a 500 makes them fail forever on a
            // tenant that was already removed out of band.
            return NotFound();
        }

        return NoContent();
    }

    /// <inheritdoc cref="ITenantService.ProvisionWithOwnerAsync"/>
    [HttpPost("provision")]
    [RemoteCommand(Invalidates = ["GetAll"])]
    [ProducesResponseType(typeof(ProvisionResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Provision(
        [FromBody] ProvisionRequest request, CancellationToken ct)
    {
        if (request.Credential is null && request.OidcIdentity is null)
            return BadRequest(new { error = "Either Credential or OidcIdentity must be provided" });
        if (request.Credential is not null && request.OidcIdentity is not null)
            return BadRequest(new { error = "Provide either Credential or OidcIdentity, not both" });

        var result = await _tenantService.ProvisionWithOwnerAsync(
            request.Slug,
            request.DisplayName,
            request.OwnerUsername,
            request.OwnerEmail,
            request.Credential,
            request.OidcIdentity,
            ct);

        return StatusCode(StatusCodes.Status201Created, result);
    }

    // ── Credential management (admin proxy for account portal) ──────────

    /// <summary>Lists passkey credentials and OIDC identities for a member subject.</summary>
    [HttpGet("{id:guid}/members/{subjectId:guid}/credentials")]
    [RemoteQuery]
    [ProducesResponseType(typeof(SubjectCredentialsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMemberCredentials(
        Guid id, Guid subjectId,
        [FromServices] IPasskeyService passkeyService,
        [FromServices] ISubjectService subjectService,
        CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        var passkeys = await passkeyService.GetCredentialsAsync(subjectId);
        var oidcIdentities = await subjectService.GetLinkedOidcIdentitiesAsync(subjectId);

        return Ok(new SubjectCredentialsDto(
            passkeys.Select(p => new PasskeyCredentialDto(p.Id, p.Label, p.CreatedAt)).ToList(),
            oidcIdentities.Select(i => new OidcIdentityDto(i.Id, i.ProviderName, i.Email)).ToList()));
    }

    /// <summary>Attaches an OIDC identity to a member subject.</summary>
    [HttpPost("{id:guid}/members/{subjectId:guid}/credentials/oidc")]
    [RemoteCommand(Invalidates = ["GetMemberCredentials"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AttachOidcIdentity(
        Guid id, Guid subjectId,
        [FromBody] AdminAttachOidcRequest request,
        [FromServices] ISubjectService subjectService,
        CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        var (outcome, _) = await subjectService.AttachOidcIdentityAsync(
            subjectId, request.ProviderId, request.OidcSubjectId, request.Issuer, request.Email);

        return outcome switch
        {
            OidcLinkOutcome.Created => NoContent(),
            OidcLinkOutcome.AlreadyLinkedToSelf => NoContent(),
            _ => BadRequest(new { error = outcome.ToString() })
        };
    }

    /// <summary>Removes a passkey credential from a member subject.</summary>
    [HttpDelete("{id:guid}/members/{subjectId:guid}/credentials/passkey/{credentialId:guid}")]
    [RemoteCommand(Invalidates = ["GetMemberCredentials"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemovePasskeyCredential(
        Guid id, Guid subjectId, Guid credentialId,
        [FromServices] ISubjectService subjectService,
        CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        var result = await subjectService.TryRemovePasskeyCredentialAsync(subjectId, credentialId);
        return result == FactorRemovalResult.Removed ? NoContent() : BadRequest(new { error = result.ToString() });
    }

    /// <summary>Removes an OIDC identity from a member subject.</summary>
    [HttpDelete("{id:guid}/members/{subjectId:guid}/credentials/oidc/{identityId:guid}")]
    [RemoteCommand(Invalidates = ["GetMemberCredentials"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveOidcIdentity(
        Guid id, Guid subjectId, Guid identityId,
        [FromServices] ISubjectService subjectService,
        CancellationToken ct)
    {
        if (!await IsCallerTenantOwnerAsync(id, ct))
            return Forbid();

        var result = await subjectService.TryRemoveOidcIdentityAsync(subjectId, identityId);
        return result == FactorRemovalResult.Removed ? NoContent() : BadRequest(new { error = result.ToString() });
    }

    /// <summary>
    /// Verifies the authenticated caller is a member of the specified tenant
    /// with the Owner role (has superuser permission).
    /// </summary>
    private async Task<bool> IsCallerTenantOwnerAsync(Guid tenantId, CancellationToken ct)
    {
        var authContext = HttpContext.Items["AuthContext"] as AuthContext;

        // Instance-key / platform-admin callers bypass ownership checks —
        // they already passed [Authorize(Roles = "platform_admin")] and have
        // full admin permissions.
        if (authContext is { IsPlatformAdmin: true, AuthType: AuthType.InstanceKey })
            return true;

        if (authContext?.SubjectId is not { } subjectId)
            return false;

        var tenant = await _tenantService.GetByIdAsync(tenantId, ct);
        if (tenant == null) return false;

        var member = tenant.Members.FirstOrDefault(m => m.SubjectId == subjectId);
        if (member == null) return false;

        return member.Roles.Any(r => r.Slug == RoleSeeds.Owner);
    }
}

public record CreateTenantRequest(string Slug, string DisplayName);
public record UpdateTenantRequest(string DisplayName, bool IsActive, bool? AllowAccessRequests = null);
public record AddMemberRequest(Guid SubjectId, List<Guid> RoleIds, List<string>? DirectPermissions = null);

public record ProvisionRequest(
    string Slug,
    string DisplayName,
    string OwnerUsername,
    string OwnerEmail,
    ProvisionCredentialData? Credential = null,
    ProvisionOidcIdentityData? OidcIdentity = null);

public record SubjectCredentialsDto(List<PasskeyCredentialDto> Passkeys, List<OidcIdentityDto> OidcIdentities);
public record PasskeyCredentialDto(Guid Id, string? DisplayName, DateTime CreatedAt);
public record OidcIdentityDto(Guid Id, string Provider, string? Email);
public record AdminAttachOidcRequest(Guid ProviderId, string OidcSubjectId, string Issuer, string? Email);
