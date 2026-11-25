using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Invites to join the tenant, and the tenant's members. A caller can send, list and revoke
/// invites, accept an invite, list members, change a member's roles or permissions, set a member's
/// 24-hour history limit or the date their access ends, remove a member from the tenant, step
/// themselves down to a more limited role, and list, change and stop the teams given access to the tenant.
/// </summary>
/// <seealso cref="IMemberInviteService"/>
/// <seealso cref="ITenantService"/>
/// <seealso cref="ITenantRoleService"/>
[ApiController]
[System.ComponentModel.DisplayName("Members and Invites")]
[Tags("Identity")]
[Route("api/v4/member-invites")]
[Produces("application/json")]
[Authorize]
public class MemberInviteController : ControllerBase
{
    private const int DefaultHistoryPageSize = 25;

    private readonly IMemberInviteService _memberInviteService;
    private readonly ITenantService _tenantService;
    private readonly ITenantRoleService _tenantRoleService;
    private readonly ITenantMemberService _tenantMemberService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantMembershipRepository _members;

    /// <summary>
    /// Initializes a new instance of <see cref="MemberInviteController"/>.
    /// </summary>
    /// <param name="memberInviteService">Service for invite token lifecycle management.</param>
    /// <param name="tenantService">Service for tenant membership operations.</param>
    /// <param name="tenantRoleService">Service for member role assignment.</param>
    /// <param name="tenantMemberService">Service for membership lookups.</param>
    /// <param name="tenantAccessor">Accessor for the current request tenant context.</param>
    /// <param name="members">The memberships and roles the member-management actions edit.</param>
    public MemberInviteController(
        IMemberInviteService memberInviteService,
        ITenantService tenantService,
        ITenantRoleService tenantRoleService,
        ITenantMemberService tenantMemberService,
        ITenantAccessor tenantAccessor,
        ITenantMembershipRepository members)
    {
        _memberInviteService = memberInviteService;
        _tenantService = tenantService;
        _tenantRoleService = tenantRoleService;
        _tenantMemberService = tenantMemberService;
        _tenantAccessor = tenantAccessor;
        _members = members ?? throw new ArgumentNullException(nameof(members));
    }

    /// <inheritdoc cref="IMemberInviteService.CreateInviteAsync"/>
    [HttpPost]
    [RequireInteractiveSession]
    [RequireScope(Scope.MembersInvite)]
    [RemoteCommand(Invalidates = ["ListInvites"])]
    [ProducesResponseType(typeof(MemberInviteResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateInvite(
        [FromBody] CreateMemberInviteRequest request, [FromServices] TimeProvider time)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
            return Unauthorized();

        if (await HttpContext.IsCallerInactiveAsync(HttpContext.RequestAborted))
            return Challenge();

        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        var limitTo24Hours = HttpContext.InheritGivenHistoryClamp(request.LimitTo24Hours);

        var accessEndsAt = HttpContext.InheritAccessEnd(AccessEndRequest.Instant(request.AccessEndsAt));
        if (AccessEndRequest.Refusal(HttpContext, accessEndsAt, time.GetUtcNow().UtcDateTime) is (var status, var detail))
            return Problem(detail: detail, statusCode: status);

        if (limitTo24Hours)
        {
            var invitePermissions = (await _tenantRoleService.GetRolePermissionsAsync(
                    _tenantAccessor.TenantId, request.RoleIds, HttpContext.RequestAborted))
                .Concat(request.DirectPermissions ?? []);
            if (MemberScopeResolver.IsExemptFromHistoryClamp(invitePermissions))
                return Problem(detail: MemberScopeResolver.ExemptFromHistoryClampDetail, statusCode: 400);
        }

        try
        {
            var result = await _memberInviteService.CreateInviteAsync(
                _tenantAccessor.TenantId,
                subjectId.Value,
                ceiling.Scopes,
                request.RoleIds,
                accessEndsAt,
                request.DirectPermissions,
                request.Label,
                request.ExpiresInDays,
                request.MaxUses,
                limitTo24Hours,
                $"{Request.PublicScheme()}://{Request.Host}");

            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (ArgumentException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400);
        }
    }

    /// <inheritdoc cref="IMemberInviteService.GetInvitesForTenantAsync"/>
    [HttpGet]
    [RequireScope(Scope.MembersInvite)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<MemberInviteInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListInvites()
    {
        var invites = await _memberInviteService.GetInvitesForTenantAsync(_tenantAccessor.TenantId);
        return Ok(invites.Select(i => i with { OutlastsCaller = HttpContext.OutlastsCallerAccess(i.AccessEndsAt) }).ToList());
    }

    /// <inheritdoc cref="IMemberInviteService.RevokeInviteAsync"/>
    [HttpDelete("{inviteId:guid}")]
    [RequireScope(Scope.MembersInvite)]
    [RemoteCommand(Invalidates = ["ListInvites"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeInvite(Guid inviteId)
    {
        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        if (ceiling.AccessEndsAt is not null
            && (await _memberInviteService.GetInvitesForTenantAsync(_tenantAccessor.TenantId))
                .FirstOrDefault(i => i.Id == inviteId) is { } invite
            && HttpContext.OutlastsCallerAccess(invite.AccessEndsAt))
            return Problem(detail: HttpContextExtensions.InviteOutlastsCallerDetail, statusCode: StatusCodes.Status403Forbidden);

        var revoked = await _memberInviteService.RevokeInviteAsync(inviteId, _tenantAccessor.TenantId);
        return revoked ? NoContent() : NotFound();
    }

    /// <inheritdoc cref="ITenantService.RemoveMemberAsync"/>
    /// <remarks>
    /// Keyed by subject, unlike the sibling <c>members/{id}</c> routes below, which take the
    /// membership id. Removal is the one operation whose caller — the cloud billing service as well
    /// as the member list — knows the subject rather than the membership.
    /// </remarks>
    [HttpDelete("members/{subjectId:guid}")]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetMembers"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveMember(Guid subjectId, CancellationToken ct)
    {
        if (IsCallersOwnMembership(subjectId))
            return Problem(detail: SelfRemovalDetail, statusCode: 400);

        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        if (ceiling.AccessEndsAt is not null
            && await _members.FindMemberAsync(_tenantAccessor.TenantId, subjectId, ct) is { } member
            && this.MemberOutlastsCallerRefusal(member.AccessEndsAt) is { } outlasts)
            return outlasts;

        var result = await _tenantService.RemoveMemberAsync(_tenantAccessor.TenantId, subjectId, ct);
        if (result.AccessFromTeam)
            return AccessFromTeamProblem();
        return result.Ok
            ? NoContent()
            : Problem(detail: result.ErrorDescription, statusCode: 400);
    }

    /// <summary>
    /// Get invite info for the accept page, along with where the caller stands relative to it.
    /// Requires no authentication. An invite that can no longer be accepted answers with the reason only.
    /// </summary>
    /// <remarks>
    /// Anonymous, because the invitee may have no account yet. A caller who does arrive signed in
    /// is reported in <see cref="JoinInviteInfo.Viewer"/> so the join page can offer acceptance
    /// instead of a second registration — including when they are signed in as a member of some
    /// other tenant on this instance, whose session cookie is domain-wide.
    /// <para>
    /// The token is the whole of the authorization, so an invite that can no longer be accepted
    /// answers with the reason alone, as the sibling alert-invite lookup does. The record it would
    /// otherwise return names the tenant, the inviter, and the roles and permissions being granted.
    /// </para>
    /// </remarks>
    [HttpGet("{token}/info")]
    [AllowAnonymous]
    [EnableRateLimiting("invite-lookup")]
    [InviteTokenAuthorized]
    [RemoteQuery]
    [ProducesResponseType(typeof(JoinInviteInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetInviteInfo([CredentialRouteValue] string token, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var invite = await _memberInviteService.GetInviteByTokenAsync(token, tenantId);
        if (invite == null)
            return NotFound();

        if (!invite.IsValid)
        {
            // 400 rather than 410, matching AcceptInvite below: the generated client passes a 400
            // ProblemDetails through to the caller, and describeSubmitError surfaces the reason,
            // whereas other 4xx statuses collapse to the generic message. Wording and order match
            // the acceptance refusal.
            var reason = invite.IsExpired ? "This invite has expired."
                : invite.IsRevoked ? "This invite has been revoked."
                : "This invite has reached its maximum uses.";

            return Problem(detail: reason, statusCode: StatusCodes.Status400BadRequest);
        }

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
            return Ok(ToJoinInfo(invite, null));

        var authContext = HttpContext.GetAuthContext();
        var isMember = await _tenantMemberService.IsDirectMemberAsync(subjectId.Value, tenantId, ct);

        return Ok(ToJoinInfo(invite, new InviteViewer(subjectId, authContext?.SubjectName, isMember)));
    }

    private static JoinInviteInfo ToJoinInfo(MemberInviteInfo invite, InviteViewer? viewer) => new(
        invite.TenantName,
        invite.CreatedByName,
        invite.RoleNames,
        invite.Permissions,
        invite.LimitTo24Hours,
        invite.ExpiresAt,
        invite.Permissions.Count > 0,
        Scope.IsViewOnlyForRecordsAndAccess(invite.Permissions),
        viewer,
        invite.AccessEndsAt);

    /// <summary>
    /// Accept an invite and join the tenant. The caller must be signed in but need not already belong to the tenant.
    /// </summary>
    /// <remarks>
    /// The caller must be signed in as some subject, but need not already belong to this tenant —
    /// that is the point of an invite. <see cref="InviteTokenAuthorizedAttribute"/> is what lets a
    /// non-member reach this action at all, and the token is re-validated against the resolved
    /// tenant by the service before any membership is written.
    /// </remarks>
    [HttpPost("{token}/accept")]
    [Authorize]
    [InviteTokenAuthorized]
    [DenyDemoSubject]
    [RemoteCommand]
    [ProducesResponseType(typeof(AcceptMemberInviteResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AcceptInvite([CredentialRouteValue] string token)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
            return Unauthorized();

        var result = await _memberInviteService.AcceptInviteAsync(
            token, subjectId.Value, _tenantAccessor.TenantId);

        // The refusal reason is written for the invitee — "You are already a member of this
        // tenant", "This invite has expired".
        if (result.ErrorCode == SelfAdmission.OwnInviteCode)
            return this.ProblemWithCode(SelfAdmission.OwnInviteCode, result.ErrorDescription, StatusCodes.Status403Forbidden);

        if (!result.Success)
            return Problem(detail: result.ErrorDescription, statusCode: 400);

        return Ok(result);
    }

    /// <summary>
    /// List all members of the current tenant. Whether each has a confirmed recovery email is reported only to a caller who holds <c>members.manage</c>.
    /// </summary>
    [HttpGet("members")]
    [RequireScope(Scope.MembersInvite, Scope.MembersManage, Scope.SharingManage)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<TenantMemberDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMembers(CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var tenant = await _tenantService.GetByIdAsync(tenantId, ct);
        if (tenant == null)
            return NotFound();

        var managesMembers = HttpContext.HasScope(Scope.MembersManage);
        return Ok(tenant.Members
            .Select(m => m with
            {
                HasConfirmedRecoveryEmail = managesMembers ? m.HasConfirmedRecoveryEmail : null,
                OutlastsCaller = HttpContext.OutlastsCallerAccess(m.AccessEndsAt),
            })
            .ToList());
    }

    /// <summary>
    /// The teams with access to the current tenant: each team's name, the roles given, the limit, the
    /// end and whether it has passed, who approved it and when.
    /// </summary>
    [HttpGet("team-access")]
    [RequireScope(Scope.MembersInvite, Scope.MembersManage, Scope.SharingManage)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<TenantTeamAccess>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTeamAccess(
        [FromServices] ITeamRepository teams, [FromServices] TimeProvider time, CancellationToken ct) =>
        Ok(await teams.ListAccessOfTenantAsync(_tenantAccessor.TenantId, time.GetUtcNow().UtcDateTime, ct));

    /// <summary>Stops a team's access to the current tenant at once.</summary>
    [HttpDelete("team-access/{teamId:guid}")]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetTeamAccess", "GetMembers", "GetMemberHistory"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StopTeamAccess(
        Guid teamId, [FromServices] TeamAccessService access, CancellationToken ct) =>
        await access.StopAsync(_tenantAccessor.TenantId, teamId, ct) ? NoContent() : NotFound();

    /// <summary>
    /// Approves a staff member of a team with access to the current tenant, so they reach it through the
    /// team. Lifts a decline. Only an owner may.
    /// </summary>
    [HttpPost("team-access/{teamId:guid}/staff/{subjectId:guid}/approve")]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetTeamAccess", "GetMembers", "GetMemberHistory"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApproveTeamStaff(
        Guid teamId, Guid subjectId, [FromServices] TeamAccessService access, CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();
        if (HttpContext.GetSubjectId() is not { } callerId)
            return Unauthorized();

        return StaffDecisionResult(await access.ApproveStaffAsync(_tenantAccessor.TenantId, teamId, callerId, subjectId, ct));
    }

    /// <summary>
    /// Declines a staff member of a team with access to the current tenant, approved or waiting, so they do
    /// not reach it through the team, whatever the team changes, until an owner approves them. Only an
    /// owner may.
    /// </summary>
    [HttpPost("team-access/{teamId:guid}/staff/{subjectId:guid}/decline")]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetTeamAccess", "GetMembers", "GetMemberHistory"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeclineTeamStaff(
        Guid teamId, Guid subjectId, [FromServices] TeamAccessService access, CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();
        if (HttpContext.GetSubjectId() is not { } callerId)
            return Unauthorized();

        return StaffDecisionResult(await access.DeclineStaffAsync(_tenantAccessor.TenantId, teamId, callerId, subjectId, ct));
    }

    /// <summary>
    /// Turns "the team manages its own staff" on or off for a team with access to the current tenant.
    /// Off keeps the staff already approved, and each new or widened staff member waits for an owner.
    /// On approves every waiting staff member at once and keeps the declined declined. Only an owner may.
    /// </summary>
    [HttpPut("team-access/{teamId:guid}/staff-rule")]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetTeamAccess", "GetMembers", "GetMemberHistory"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetTeamStaffRule(
        Guid teamId, [FromBody] SetTeamStaffRuleRequest request, [FromServices] TeamAccessService access, CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();
        if (HttpContext.GetSubjectId() is not { } callerId)
            return Unauthorized();

        return StaffDecisionResult(
            await access.SetStaffRuleAsync(_tenantAccessor.TenantId, teamId, callerId, request.TeamManagesStaff, ct));
    }

    /// <summary>
    /// Sets what a team's access to the current tenant gives: its roles, the 24-hour limit and the end, sent
    /// whole. Bounded by the caller's own access and credential, as an approval is, and not by the link the
    /// access was made from. No staff member is sent back to waiting, and each staff decision and the switch
    /// stay as they are. Only an owner may.
    /// </summary>
    [HttpPut("team-access/{teamId:guid}")]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetTeamAccess", "GetMembers", "GetMemberHistory"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EditTeamAccess(
        Guid teamId, [FromBody] EditTeamAccessRequest request, [FromServices] TeamAccessService access, CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();
        if (HttpContext.GetSubjectId() is not { } callerId || HttpContext.GetAuthContext() is not { } auth)
            return Unauthorized();
        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        var caller = new TeamAccessApproverCredential(callerId, auth.AuthType, ceiling.Scopes, ceiling.HistoryClamped);
        var terms = new TeamAccessTerms(request.RoleIds, request.LimitTo24Hours, AccessEndRequest.Instant(request.AccessEndsAt));
        return await access.EditAccessAsync(_tenantAccessor.TenantId, teamId, caller, terms, ct) switch
        {
            { Decision: OwnerDecision.Decided, Refusal: { } refusal } => this.TeamAccessProblem(refusal),
            { Decision: OwnerDecision.Decided } => NoContent(),
            { Decision: OwnerDecision.AccessFromTeam } => this.CallerAccessFromTeamProblem(),
            { Decision: OwnerDecision.NotOwner } => this.TeamAccessProblem(TeamAccessRefusal.NotOwner),
            _ => NotFound(),
        };
    }

    /// <summary>
    /// A page of the changes to what the current tenant's members can read, newest first: members
    /// joining and leaving, roles and direct permissions given or taken, history limits, end dates, and
    /// the permissions a role grants. Narrow it to one member with <c>memberSubjectId</c>, which also
    /// says when that member's access ended.
    /// </summary>
    [HttpGet("members/history")]
    [RequireScope(Scope.MembersManage)]
    [RemoteQuery]
    [ProducesResponseType(typeof(MemberHistoryPage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MemberHistoryPage>> GetMemberHistory(
        [FromServices] IMemberHistoryReader history,
        [FromServices] TimeProvider time,
        [FromQuery] Guid? memberSubjectId = null,
        [FromQuery] int limit = DefaultHistoryPageSize,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var page = await history.GetAsync(
            _tenantAccessor.TenantId,
            memberSubjectId,
            V4ReadLimits.ClampPageSize(limit),
            V4ReadLimits.ClampOffset(offset),
            time.GetUtcNow().UtcDateTime,
            ct);
        return Ok(page);
    }

    /// <summary>
    /// Set roles for a member. <c>RoleIds</c> is the member's whole role set after the change.
    /// Only the change is checked against the caller's grant ceiling, so a role the caller could not grant but the member already holds must stay in the set; removing it is refused like adding it.
    /// </summary>
    /// <remarks>
    /// Only the change is checked against the caller's grant ceiling, so a caller who could not
    /// grant a role the member already holds can still edit the member's other roles. Such a role
    /// must stay in the set: removing it is refused like adding it.
    /// </remarks>
    [HttpPut("members/{id:guid}/roles")]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetMembers"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetMemberRoles(
        Guid id,
        [FromBody] SetMemberRolesRequest request,
        [FromServices] PublicAccessCacheService publicAccessCache,
        CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        var tenantId = _tenantAccessor.TenantId;
        var member = await _members.GetMemberAsync(tenantId, id, ct);

        if (member == null)
            return NotFound();

        if (member.DirectReason is not { } direct)
            return AccessFromTeamProblem();

        if (member.IsPublicSubject() && this.PublicSharingEndDateRefusal() is { } dated)
            return dated;

        if (this.MemberOutlastsCallerRefusal(member.AccessEndsAt) is { } outlasts)
            return outlasts;

        if (IsCallersOwnMembership(member.SubjectId))
            return Problem(detail: SelfEditDetail, statusCode: 400);

        if (DeviceHolderDetail(member) is { } deviceHolder)
            return Problem(detail: deviceHolder, statusCode: 400);

        if (request.RoleIds.Count == 0 && (direct.DirectPermissions == null || direct.DirectPermissions.Count == 0))
        {
            const string reason = "Cannot remove all roles when member has no direct permissions";
            return Problem(detail: reason, statusCode: 400);
        }

        var held = direct.Roles.Select(r => r.RoleId).ToList();
        var added = request.RoleIds.Except(held).ToList();
        var removed = held.Except(request.RoleIds).ToList();
        var grantedScopes = ceiling.Scopes;

        var roleGrant = await _tenantRoleService.ValidateRoleGrantAsync(tenantId, added, grantedScopes, ct);
        if (!roleGrant.Ok)
            return RoleGrantProblem(roleGrant);

        if (!(await _tenantRoleService.ValidateRoleGrantAsync(tenantId, removed, grantedScopes, ct)).Ok)
            return Problem(detail: UngrantableRoleRemovalDetail, statusCode: 403);

        var rolesAfter = await _tenantRoleService.GetRolePermissionsAsync(tenantId, request.RoleIds, ct);
        if (await WouldExemptAClampedMemberAsync(
                member, rolesAfter.Concat(direct.DirectPermissions ?? []).Concat(member.PermissionsBesideDirect), ct))
            return Problem(detail: HttpContextExtensions.HistoryCeilingDetail, statusCode: 403);

        switch (await _tenantService.ReplaceMemberRolesAsync(tenantId, member.Id, removed, added, ct))
        {
            case OwnerGuardedChange.LastOwner:
                return Problem(detail: TenantOwnerFilter.LastOwnerRefusal, statusCode: 400);
            case OwnerGuardedChange.OwnerAccessEnds:
                return Problem(detail: AccessEndRequest.ClearBeforeOwnerDetail, statusCode: 400);
            case OwnerGuardedChange.FromTeam:
                return AccessFromTeamProblem();
        }

        if (member.IsPublicSubject())
            publicAccessCache.Evict(tenantId);

        return NoContent();
    }

    /// <summary>
    /// Replaces the caller's own roles with <see cref="RoleSeeds.Caretaker"/>, for a helper handing
    /// the tenant over. Direct permissions and the history clamp stay as they are.
    /// Refused unless the caller could grant that role.
    /// </summary>
    /// <remarks>
    /// The one self-edit allowed past <see cref="IsCallersOwnMembership"/> and
    /// <see cref="SelfEditDetail"/>. It can only narrow: it is refused unless caretaker is within what
    /// the caller may give (<see cref="HttpContextExtensions.CallerGrantCeiling"/>) and within the
    /// direct reason it writes.
    /// </remarks>
    [HttpPost("members/self/step-down")]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StepDown(CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var tenantId = _tenantAccessor.TenantId;
        var member = await _members.FindMemberAsync(tenantId, subjectId, ct);
        var caretakerId = await _members.FindRoleIdBySlugAsync(tenantId, RoleSeeds.Caretaker, ct);
        if (member == null || caretakerId is not { } caretaker)
            return NotFound();

        if (member.DirectReason is not { } direct)
            return AccessFromTeamProblem();

        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        if (!(await _tenantRoleService.ValidateRoleGrantAsync(
                tenantId, [caretaker], ceiling.Scopes, ct)).Ok
            || !(await _tenantRoleService.ValidateRoleGrantAsync(
                tenantId, [caretaker], Scope.NormalizeMemberPermissions(direct.EffectivePermissions()), ct)).Ok)
            return Problem(detail: "Cannot step down to a role with more access than you have.", statusCode: 403);

        var removed = direct.Roles.Where(r => r.RoleId != caretaker).ToList();
        var assigned = direct.Roles.All(r => r.RoleId != caretaker);
        return await _tenantService.ReplaceMemberRolesAsync(
                tenantId, member.Id, removed.Select(r => r.RoleId).ToList(), assigned ? [caretaker] : [], ct) switch
        {
            OwnerGuardedChange.LastOwner => Problem(detail: TenantOwnerFilter.LastOwnerRefusal, statusCode: 400),
            OwnerGuardedChange.FromTeam => AccessFromTeamProblem(),
            _ => NoContent(),
        };
    }

    /// <summary>
    /// Set direct permissions for a member.
    /// </summary>
    [HttpPut("members/{id:guid}/permissions")]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetMembers"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetMemberPermissions(
        Guid id,
        [FromBody] SetMemberPermissionsRequest request,
        [FromServices] PublicAccessCacheService publicAccessCache,
        CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        var tenantId = _tenantAccessor.TenantId;
        var member = await _members.GetMemberAsync(tenantId, id, ct);

        if (member == null)
            return NotFound();

        if (member.DirectReason is not { } direct)
            return AccessFromTeamProblem();

        if (member.IsPublicSubject() && this.PublicSharingEndDateRefusal() is { } dated)
            return dated;

        if (this.MemberOutlastsCallerRefusal(member.AccessEndsAt) is { } outlasts)
            return outlasts;

        if (IsCallersOwnMembership(member.SubjectId))
            return Problem(detail: SelfEditDetail, statusCode: 400);

        if (DeviceHolderDetail(member) is { } deviceHolder)
            return Problem(detail: deviceHolder, statusCode: 400);

        if ((request.DirectPermissions == null || request.DirectPermissions.Count == 0) && direct.Roles.Count == 0)
        {
            const string reason = "Cannot remove all permissions when member has no roles";
            return Problem(detail: reason, statusCode: 400);
        }

        // The Public system subject serves the anonymous share viewer, so the granter's own
        // permissions are the wrong bound: an owner holding "*" would otherwise be able to give an
        // unauthenticated reader everything. ShareLinkService.SetScopesAsync bounds the same rows to
        // PublicShareScopes; both writers have to agree or the narrower one is decorative.
        var isPublicSubject = member.IsPublicSubject();
        if (isPublicSubject)
        {
            var outsideShareVocabulary = (request.DirectPermissions ?? [])
                .Where(p => !Scope.PublicShareScopes.Contains(p))
                .ToList();
            if (outsideShareVocabulary.Count > 0)
            {
                var reason = $"Public access cannot be granted: {string.Join(", ", outsideShareVocabulary)}.";
                return Problem(detail: reason, statusCode: 400);
            }
        }
        else
        {
            var violation = Scope.ValidateGrant(request.DirectPermissions, ceiling.Scopes);
            if (violation != null)
                return GrantProblem(violation);
        }

        var rolePermissions = await _tenantRoleService.GetRolePermissionsAsync(
            tenantId, direct.Roles.Select(r => r.RoleId).ToList(), ct);
        if (await WouldExemptAClampedMemberAsync(
                member, rolePermissions.Concat(request.DirectPermissions ?? []).Concat(member.PermissionsBesideDirect), ct))
            return Problem(detail: HttpContextExtensions.HistoryCeilingDetail, statusCode: 403);

        await _members.SetDirectPermissionsAsync(tenantId, member.Id, request.DirectPermissions, DateTime.UtcNow, ct);

        if (isPublicSubject)
            publicAccessCache.Evict(tenantId);

        return NoContent();
    }

    /// <summary>
    /// Get effective permissions for a member (union of role permissions + direct permissions).
    /// </summary>
    [HttpGet("members/{id:guid}/effective-permissions")]
    [RequireScope(Scope.SharingManage)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEffectivePermissions(Guid id, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        if (await _members.GetMemberAsync(tenantId, id, ct) == null)
            return NotFound();

        var permissions = await _tenantRoleService.GetEffectivePermissionsAsync(id, ct);
        return Ok(permissions);
    }

    /// <summary>
    /// Update the 24-hour data limit for a member.
    /// </summary>
    [HttpPut("members/{id:guid}/limit-to-24-hours")]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetMembers"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetMemberLimitTo24Hours(
        Guid id,
        [FromBody] SetMemberLimitTo24HoursRequest request,
        [FromServices] PublicAccessCacheService publicAccessCache,
        CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        var tenantId = _tenantAccessor.TenantId;
        var member = await _members.GetMemberAsync(tenantId, id, ct);

        if (member == null)
            return NotFound();

        if (member.DirectReason is not { } direct)
            return AccessFromTeamProblem();

        if (member.IsPublicSubject() && this.PublicSharingEndDateRefusal() is { } dated)
            return dated;

        if (this.MemberOutlastsCallerRefusal(member.AccessEndsAt) is { } outlasts)
            return outlasts;

        // The clamp is enforced in RLS via app.history_clamped, so lifting your own is a
        // self-widening edit — the same class the role and permission editors refuse.
        if (IsCallersOwnMembership(member.SubjectId))
            return Problem(detail: SelfEditDetail, statusCode: 400);

        if (DeviceHolderDetail(member) is { } deviceHolder)
            return Problem(detail: deviceHolder, statusCode: 400);

        if (!request.LimitTo24Hours && ceiling.HistoryClamped)
            return Problem(detail: HttpContextExtensions.HistoryCeilingDetail, statusCode: 403);

        if (request.LimitTo24Hours
            && MemberScopeResolver.IsExemptFromHistoryClamp(
                member.HeldReasons.Count == 1
                    ? await _tenantRoleService.GetEffectivePermissionsAsync(id, ct)
                    : direct.EffectivePermissions()))
            return Problem(detail: MemberScopeResolver.ExemptFromHistoryClampDetail, statusCode: 400);

        await _members.SetLimitTo24HoursAsync(tenantId, member.Id, request.LimitTo24Hours, DateTime.UtcNow, ct);

        if (member.IsPublicSubject())
            publicAccessCache.Evict(tenantId);

        return NoContent();
    }


    /// <summary>
    /// Set when a member's access ends, or clear it with null. From that instant the membership
    /// grants nothing, and nothing is revoked, so a later date restores the same access.
    /// </summary>
    /// <remarks>
    /// To end access now, remove the member: an end at or before now is refused. A caller whose own
    /// access ends cannot set a later end or clear one, nor change a member whose access outlasts theirs
    /// (<see cref="HttpContextExtensions.OutlastsCallerAccess"/>).
    /// An owner cannot be given an end date.
    /// </remarks>
    [HttpPut("members/{id:guid}/access-end")]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetMembers"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetMemberAccessEnd(
        Guid id,
        [FromBody] SetMemberAccessEndRequest request,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        if (HttpContext.CallerGrantCeiling() is null)
            return this.CallerAccessFromTeamProblem();

        var tenantId = _tenantAccessor.TenantId;
        var member = await _members.GetMemberAsync(tenantId, id, ct);

        if (member == null)
            return NotFound();

        if (member.DirectReason is null)
            return AccessFromTeamProblem();

        if (this.MemberOutlastsCallerRefusal(member.AccessEndsAt) is { } outlasts)
            return outlasts;

        if (IsCallersOwnMembership(member.SubjectId))
            return Problem(detail: SelfEditDetail, statusCode: 400);

        if (member.IsSystemSubject)
            return Problem(detail: AccessEndRequest.SystemSubjectDetail, statusCode: 400);

        var now = time.GetUtcNow().UtcDateTime;
        var accessEndsAt = AccessEndRequest.Instant(request.AccessEndsAt);
        if (AccessEndRequest.Refusal(HttpContext, accessEndsAt, now) is (var status, var detail))
            return Problem(detail: detail, statusCode: status);

        return await _tenantService.SetMemberAccessEndAsync(tenantId, member.Id, accessEndsAt, now, ct) switch
        {
            OwnerGuardedChange.NotFound => NotFound(),
            OwnerGuardedChange.OwnerAccessEnds => Problem(detail: AccessEndRequest.OwnerDetail, statusCode: 400),
            OwnerGuardedChange.FromTeam => AccessFromTeamProblem(),
            _ => NoContent(),
        };
    }

    private const string SelfEditDetail =
        "Cannot change your own roles or permissions; ask another member with members.manage.";

    private const string SelfRemovalDetail =
        "Cannot remove your own membership; ask another member with members.manage.";

    private const string UngrantableRoleRemovalDetail = "Cannot remove a role you cannot grant.";

    /// <summary>
    /// The refusal of a change to a member who holds no direct reason. The member's access comes from
    /// a team, and the owner's lever is stopping that team's access, not this list.
    /// </summary>
    private ObjectResult AccessFromTeamProblem() =>
        this.ProblemWithCode(MemberAccessFromTeam.Code, MemberAccessFromTeam.Detail, StatusCodes.Status409Conflict);

    private IActionResult StaffDecisionResult(OwnerDecision decision) => decision switch
    {
        OwnerDecision.Decided => NoContent(),
        OwnerDecision.AccessFromTeam => this.CallerAccessFromTeamProblem(),
        OwnerDecision.NotOwner => this.ProblemWithCode(
            TeamProblemCodes.StaffDecisionNotOwner,
            "Only an owner of the site can decide which of a team's staff have access to it.",
            StatusCodes.Status403Forbidden),
        _ => NotFound(),
    };

    /// <summary>
    /// True when a history-clamped caller's edit would leave a clamped member holding permissions
    /// that exempt it (<see cref="MemberScopeResolver.IsExemptFromHistoryClamp"/>), lifting the
    /// clamp sideways. See <see cref="HttpContextExtensions.CallerGrantCeiling"/>.
    /// </summary>
    private async Task<bool> WouldExemptAClampedMemberAsync(
        TenantMemberRecord member, IEnumerable<string> permissionsAfter, CancellationToken ct) =>
        member.LimitTo24Hours
        && HttpContext.CallerGrantCeiling() is not { HistoryClamped: false }
        && MemberScopeResolver.IsExemptFromHistoryClamp(permissionsAfter)
        && !MemberScopeResolver.IsExemptFromHistoryClamp(
            await _tenantRoleService.GetEffectivePermissionsAsync(member.Id, ct));

    /// <summary>
    /// True when the target membership belongs to the calling subject. Role and permission
    /// edits are routed away from the caller's own membership: a self-edit is the only way a
    /// grant decision can widen the granter's own ceiling within a request, and it is also the
    /// path by which a sole owner can strip their own administrative access. Removal is refused
    /// too: it signs the caller out of the tenant with no way back in, and it
    /// passes the last-owner check whenever another owner row exists, whether or not that owner can sign in.
    /// </summary>
    private bool IsCallersOwnMembership(Guid targetSubjectId)
        => HttpContext.GetSubjectId() is { } subjectId && targetSubjectId == subjectId;

    /// <summary>
    /// An unknown permission is malformed input; exceeding the ceiling is a refusal. Either way the
    /// description is what the granter reads.
    /// </summary>
    private ObjectResult GrantProblem(GrantCeilingViolation violation) =>
        violation.Code == GrantCeilingViolation.UnknownPermission
            ? Problem(detail: violation.Description, statusCode: 400)
            : Problem(detail: violation.Description, statusCode: 403);

    /// <summary>
    /// A foreign role id is malformed input; a role conferring more than the caller holds is a refusal.
    /// </summary>
    private ObjectResult RoleGrantProblem(RoleGrantValidation validation) =>
        validation.ErrorCode == RoleGrantValidation.ForeignRole
            ? Problem(detail: validation.ErrorDescription, statusCode: 400)
            : Problem(detail: validation.ErrorDescription, statusCode: 403);

    /// <summary>
    /// Why <paramref name="member"/> may not be edited here, or null when it may be.
    /// </summary>
    /// <remarks>
    /// The device holder carries every API token on the tenant, and
    /// <c>MemberScopeMiddleware</c> intersects each token's scopes with this one membership. Editing
    /// it caps or clamps every uploader on the site at once, from a screen that shows one row and
    /// says nothing about the blast radius. Tokens are bounded individually, on the grant.
    /// </remarks>
    private static string? DeviceHolderDetail(TenantMemberRecord member) =>
        member is { IsSystemSubject: true, SubjectName: DeviceSubjectFilter.DeviceSubjectName }
            ? "This member holds the site's API tokens. Change what a token can do on the token itself."
            : null;
}

public class CreateMemberInviteRequest
{
    public List<Guid> RoleIds { get; set; } = [];
    public List<string>? DirectPermissions { get; set; }
    public string? Label { get; set; }
    public int ExpiresInDays { get; set; } = 7;
    public int? MaxUses { get; set; }
    public bool LimitTo24Hours { get; set; }

    /// <summary>When the membership the invite creates ends; none when omitted, unless the inviter's own access ends.</summary>
    public DateTimeOffset? AccessEndsAt { get; set; }
}

public record SetMemberRolesRequest(List<Guid> RoleIds);
public record SetMemberPermissionsRequest(List<string>? DirectPermissions);
public record SetMemberLimitTo24HoursRequest(bool LimitTo24Hours);

/// <param name="TeamManagesStaff">Whether the team's staff are approved at the site as they join.</param>
public record SetTeamStaffRuleRequest(bool TeamManagesStaff);

/// <summary>Everything a team's access gives after an owner's edit. Each member must be sent; it is not a patch.</summary>
public sealed record EditTeamAccessRequest
{
    public required IReadOnlyList<Guid> RoleIds { get; init; }

    public required bool LimitTo24Hours { get; init; }

    /// <summary>When the access ends. Absent or null means the access does not end.</summary>
    public DateTimeOffset? AccessEndsAt { get; init; }
}

/// <param name="AccessEndsAt">When the membership ends; null when it does not.</param>
public record SetMemberAccessEndRequest(DateTimeOffset? AccessEndsAt);
