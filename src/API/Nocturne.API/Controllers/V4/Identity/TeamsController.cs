using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.RateLimiting;
using Nocturne.API.Services.Identity;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// The teams the signed-in person is a member of, and running them: creating one, renaming it,
/// adding managers and staff by invite link, removing them, making links that let an owner of a site
/// give the team access to it, and deleting it. Also the other side of such a link: the person who
/// opens it reads it and approves it for a site they own.
/// </summary>
/// <remarks>
/// Run by a person in a browser session, never by an API key or a device token
/// (<see cref="RequireInteractiveSessionAttribute"/>). A team belongs to no tenant, so these routes are
/// served on every host. A caller who is not a member of a team gets 404 on every route that names it,
/// so a team's existence is never revealed to someone outside it. A staff member gets the same answer
/// as a non-manager on every route that only a manager may use.
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/me/teams")]
[Produces("application/json")]
[Authorize]
[RequireInteractiveSession]
[DenyDemoSubject]
[ErrorEnvelope]
public class TeamsController(
    ITeamRepository teams,
    IPlatformPolicyRepository policies,
    IJwtService jwtService,
    TimeProvider time) : ControllerBase
{
    /// <summary>The web path a manager invite link opens, with the token in <see cref="TokenQueryParameter"/>.</summary>
    public const string JoinPath = "/teams/join";

    public const string TokenQueryParameter = "token";

    /// <summary>The web path a staff invite link opens, with the token in <see cref="TokenQueryParameter"/>.</summary>
    public const string StaffJoinPath = "/teams/join-staff";

    /// <summary>The web path an access link opens, with the token in <see cref="TokenQueryParameter"/>.</summary>
    public const string AccessPath = "/teams/access";

    public const int MaxLabelLength = 255;

    internal const string CreationRestrictedDetail = "Only a platform administrator can create a team on this site.";

    /// <summary>The caller's teams, and whether they may create one.</summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(MyTeamsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyTeams(CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return Ok(new MyTeamsResponse(
            await teams.GetTeamsOfAsync(subjectId, ct),
            CanCreate(await policies.GetAsync(ct), HttpContext.GetAuthContext())));
    }

    /// <summary>Creates a team, with the caller as its manager.</summary>
    [HttpPost]
    [LimitPerPerson<TeamCreationLimiter>]
    [RemoteCommand(Invalidates = ["GetMyTeams"])]
    [ProducesResponseType(typeof(TeamSummary), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateTeam([FromBody] TeamNameRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        if (!CanCreate(await policies.GetAsync(ct), HttpContext.GetAuthContext()))
            return this.ProblemWithCode(
                TeamProblemCodes.CreationRestricted, CreationRestrictedDetail, StatusCodes.Status403Forbidden);

        if (TeamName(request.Name) is not { } name)
            return NameProblem();

        var team = await teams.CreateAsync(subjectId, name, ct);
        return StatusCode(StatusCodes.Status201Created, team);
    }

    /// <summary>
    /// The team's name, managers, and what the caller may do to it. A manager also reads the managers'
    /// subject ids, the staff, the live manager and staff invites and access links, and the sites the
    /// team has access to: their names, the roles given, the limit and the end, never who approved or
    /// who is in the site. A staff member reads the name, the managers' names, what narrows them and
    /// the static list of roles a ceiling may name, and nothing else.
    /// </summary>
    [HttpGet("{teamId:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TeamDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTeam(Guid teamId, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return await teams.GetAsync(teamId, subjectId, Now, ct) is { } team ? Ok(team) : NotFound();
    }

    /// <summary>
    /// Renames the team, and tells each person who gave it access to a site that is still theirs.
    /// </summary>
    [HttpPut("{teamId:guid}/name")]
    [LimitPerPerson<TeamRenameLimiter>]
    [RemoteCommand(Invalidates = ["GetMyTeams", "GetTeam"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RenameTeam(
        Guid teamId, [FromBody] TeamNameRequest request, [FromServices] TeamRenameAnnouncer announcer, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        if (TeamName(request.Name) is not { } name)
            return NameProblem();

        if (await teams.RenameAsync(teamId, subjectId, name, ct) is not { } oldName)
            return NotFound();

        if (oldName != name)
            await announcer.AnnounceAsync(teamId, oldName);
        return NoContent();
    }

    /// <summary>
    /// Removes a manager from the team, and revokes the invites they made. The caller's own id means
    /// the caller leaves. Refused while the manager is the one the team cannot lose
    /// (<see cref="TeamDetail.IsLastManager"/>).
    /// </summary>
    [HttpDelete("{teamId:guid}/managers/{subjectId:guid}")]
    [RemoteCommand(Invalidates = ["GetMyTeams", "GetTeam"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveManager(Guid teamId, Guid subjectId, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } callerId)
            return Unauthorized();

        return await teams.RemoveManagerAsync(teamId, callerId, subjectId, Now, ct) switch
        {
            ManagerGuardedChange.Changed => NoContent(),
            ManagerGuardedChange.LastManager => this.ProblemWithCode(
                TeamProblemCodes.LastManager, TeamManagerFilter.LastManagerRefusal, StatusCodes.Status400BadRequest),
            _ => NotFound(),
        };
    }

    /// <summary>
    /// Makes a link that makes whoever opens it, signed in, a manager of the team. The token is in
    /// this response only.
    /// </summary>
    [HttpPost("{teamId:guid}/invites")]
    [RemoteCommand(Invalidates = ["GetTeam"])]
    [ProducesResponseType(typeof(TeamInviteCreated), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateManagerInvite(
        Guid teamId, [FromBody] CreateTeamInviteRequest request, CancellationToken ct)
    {
        if (await HttpContext.IsCallerSubjectInactiveAsync(ct))
            return Challenge();

        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        if (LinkProblem(request.ExpiresInDays, request.MaxUses, request.Label, out var label) is { } problem)
            return problem;

        var token = jwtService.GenerateRefreshToken();
        var expiresAt = Now.AddDays(request.ExpiresInDays);
        var inviteId = await teams.CreateInviteAsync(
            teamId, subjectId, new NewTeamInvite(HashUtils.Sha256Hex(token), label, expiresAt, request.MaxUses), ct);
        if (inviteId is not { } id)
            return NotFound();

        return StatusCode(StatusCodes.Status201Created, new TeamInviteCreated(id, token, LinkUrl(JoinPath, token), expiresAt));
    }

    /// <summary>Revokes one of the team's manager invites.</summary>
    [HttpDelete("{teamId:guid}/invites/{inviteId:guid}")]
    [RemoteCommand(Invalidates = ["GetTeam"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeManagerInvite(Guid teamId, Guid inviteId, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return await teams.RevokeInviteAsync(teamId, subjectId, inviteId, Now, ct) ? NoContent() : NotFound();
    }

    /// <summary>
    /// The team a manager invite is for and who made it, for the signed-in person holding its token.
    /// An invite that can no longer be accepted answers with the reason alone.
    /// </summary>
    [HttpGet("invites/{token}")]
    [EnableRateLimiting("invite-lookup")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TeamInviteDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInviteInfo([CredentialRouteValue] string token, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var info = await teams.GetInviteInfoAsync(HashUtils.Sha256Hex(token), subjectId, Now, ct);
        if (info is null)
            return NotFound();

        if (UnusableDetail(info.Refusal) is { } detail)
            return this.ProblemWithCode(UnusableCode(info.Refusal)!, detail, StatusCodes.Status400BadRequest);

        return Ok(new TeamInviteDetails(
            info.TeamId, info.TeamName, info.CreatedByName, info.Kind, info.Refusal == TeamInviteRefusal.AlreadyMember));
    }

    /// <summary>
    /// Makes the caller a manager of the invite's team. A caller already a member is told so, and
    /// nothing changes.
    /// </summary>
    [HttpPost("invites/{token}/accept")]
    [EnableRateLimiting("invite-lookup")]
    [RemoteCommand(Invalidates = ["GetMyTeams", "GetInviteInfo"])]
    [ProducesResponseType(typeof(TeamInviteAccepted), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcceptInvite([CredentialRouteValue] string token, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return AcceptanceResult(await teams.AcceptInviteAsync(HashUtils.Sha256Hex(token), subjectId, Now, ct));
    }

    /// <summary>
    /// Makes a link that makes whoever opens it, signed in, staff of the team, within the role ceiling,
    /// 24-hour limit and end it gives. The token is in this response only.
    /// </summary>
    [HttpPost("{teamId:guid}/staff-invites")]
    [RemoteCommand(Invalidates = ["GetTeam"])]
    [ProducesResponseType(typeof(TeamStaffInviteCreated), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateStaffInvite(
        Guid teamId, [FromBody] CreateTeamStaffInviteRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        if (LinkProblem(request.ExpiresInDays, request.MaxUses, request.Label, out var label) is { } problem)
            return problem;

        if (NarrowingProblem(request.RoleCeilingSlug, request.LimitTo24Hours, request.StaffEndsAt, refusePassedEnd: true, out var narrowing) is { } narrowingProblem)
            return narrowingProblem;

        var token = jwtService.GenerateRefreshToken();
        var expiresAt = Now.AddDays(request.ExpiresInDays);
        var inviteId = await teams.CreateStaffInviteAsync(
            teamId, subjectId, new NewTeamStaffInvite(HashUtils.Sha256Hex(token), label, expiresAt, request.MaxUses, narrowing!), ct);
        if (inviteId is not { } id)
            return NotFound();

        return StatusCode(StatusCodes.Status201Created, new TeamStaffInviteCreated(id, token, LinkUrl(StaffJoinPath, token), expiresAt));
    }

    /// <summary>Revokes one of the team's staff invites.</summary>
    [HttpDelete("{teamId:guid}/staff-invites/{inviteId:guid}")]
    [RemoteCommand(Invalidates = ["GetTeam"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeStaffInvite(Guid teamId, Guid inviteId, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return await teams.RevokeStaffInviteAsync(teamId, subjectId, inviteId, Now, ct) ? NoContent() : NotFound();
    }

    /// <summary>
    /// The team a staff invite is for, who made it and what narrows the staff member it makes, for the
    /// signed-in person holding its token. An invite that can no longer be accepted answers with the
    /// reason alone.
    /// </summary>
    [HttpGet("staff-invites/{token}")]
    [EnableRateLimiting("invite-lookup")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TeamStaffInviteDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStaffInviteInfo([CredentialRouteValue] string token, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var info = await teams.GetStaffInviteInfoAsync(HashUtils.Sha256Hex(token), subjectId, Now, ct);
        if (info is null)
            return NotFound();

        if (UnusableDetail(info.Refusal) is { } detail)
            return this.ProblemWithCode(UnusableCode(info.Refusal)!, detail, StatusCodes.Status400BadRequest);

        return Ok(new TeamStaffInviteDetails(
            info.TeamId,
            info.TeamName,
            info.CreatedByName,
            info.Narrowing,
            info.Refusal == TeamInviteRefusal.AlreadyMember,
            TeamSuggestableRoles.All));
    }

    /// <summary>
    /// Makes the caller staff of the invite's team. A caller already a member, of either kind, is told
    /// so, and nothing changes. The caller reaches each site the team follows and has approved them, within
    /// what narrows them.
    /// </summary>
    [HttpPost("staff-invites/{token}/accept")]
    [EnableRateLimiting("invite-lookup")]
    [RemoteCommand(Invalidates = ["GetMyTeams", "GetStaffInviteInfo"])]
    [ProducesResponseType(typeof(TeamInviteAccepted), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcceptStaffInvite(
        [CredentialRouteValue] string token, [FromServices] TeamAccessService access, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return AcceptanceResult(await access.JoinStaffAsync(HashUtils.Sha256Hex(token), subjectId, ct));
    }

    /// <summary>
    /// Sets what narrows a staff member: the role ceiling, the 24-hour limit and the end. A change that
    /// gives them more than they held sends the sites that decide for themselves back to waiting.
    /// </summary>
    [HttpPut("{teamId:guid}/staff/{subjectId:guid}")]
    [RemoteCommand(Invalidates = ["GetTeam"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStaff(
        Guid teamId, Guid subjectId, [FromBody] UpdateTeamStaffRequest request, [FromServices] TeamAccessService access,
        CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } callerId)
            return Unauthorized();

        if (NarrowingProblem(request.RoleCeilingSlug, request.LimitTo24Hours, request.EndsAt, refusePassedEnd: false, out var narrowing) is { } problem)
            return problem;

        return await access.UpdateStaffAsync(teamId, callerId, subjectId, narrowing!, ct) switch
        {
            TeamStaffChange.Changed => NoContent(),
            TeamStaffChange.EndPassed => PassedEndProblem(),
            _ => NotFound(),
        };
    }

    /// <summary>
    /// Removes a staff member from the team. The caller's own id means the caller leaves, which a staff
    /// member may do; removing anyone else is for a manager.
    /// </summary>
    [HttpDelete("{teamId:guid}/staff/{subjectId:guid}")]
    [RemoteCommand(Invalidates = ["GetMyTeams", "GetTeam"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveStaff(
        Guid teamId, Guid subjectId, [FromServices] TeamAccessService access, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } callerId)
            return Unauthorized();

        return await access.RemoveStaffAsync(teamId, callerId, subjectId, ct) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Makes a link that lets whoever opens it, signed in, give the team access to a site they own,
    /// within the role, limit and end it suggests. The token is in this response only.
    /// </summary>
    [HttpPost("{teamId:guid}/access-links")]
    [RemoteCommand(Invalidates = ["GetTeam"])]
    [ProducesResponseType(typeof(TeamAccessLinkCreated), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateAccessLink(
        Guid teamId, [FromBody] CreateTeamAccessLinkRequest request, CancellationToken ct)
    {
        if (await HttpContext.IsCallerSubjectInactiveAsync(ct))
            return Challenge();

        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        if (LinkProblem(request.ExpiresInDays, request.MaxUses, request.Label, out var label) is { } problem)
            return problem;

        if (TeamSuggestableRoles.PermissionsOf(request.RoleSlug) is null)
            return this.ProblemWithCode(
                TeamProblemCodes.AccessRoleInvalid,
                "The role must be one an access link may suggest.",
                StatusCodes.Status400BadRequest);

        var suggestedEnd = AccessEndRequest.Instant(request.AccessEndsAt);
        if (suggestedEnd is { } end && MembershipLiveness.HasEnded(end, Now))
            return this.TeamAccessProblem(TeamAccessRefusal.EndInPast);

        var token = jwtService.GenerateRefreshToken();
        var expiresAt = Now.AddDays(request.ExpiresInDays);
        var suggestion = new TeamAccessSuggestion(request.RoleSlug, request.LimitTo24Hours, suggestedEnd);
        var linkId = await teams.CreateAccessLinkAsync(
            teamId, subjectId, new NewTeamAccessLink(HashUtils.Sha256Hex(token), label, expiresAt, request.MaxUses, suggestion), ct);
        if (linkId is not { } id)
            return NotFound();

        return StatusCode(StatusCodes.Status201Created, new TeamAccessLinkCreated(id, token, LinkUrl(AccessPath, token), expiresAt));
    }

    /// <summary>Revokes one of the team's access links. Access already given stays.</summary>
    [HttpDelete("{teamId:guid}/access-links/{linkId:guid}")]
    [RemoteCommand(Invalidates = ["GetTeam"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeAccessLink(Guid teamId, Guid linkId, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return await teams.RevokeAccessLinkAsync(teamId, subjectId, linkId, Now, ct) ? NoContent() : NotFound();
    }

    /// <summary>
    /// The team an access link is for, who made it, what it suggests, and the caller's own sites they
    /// may give the team access to. A link that can no longer be used answers with the reason alone.
    /// </summary>
    [HttpGet("access-links/{token}")]
    [EnableRateLimiting("invite-lookup")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TeamAccessLinkDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccessLinkInfo(
        [CredentialRouteValue] string token, [FromServices] TeamAccessService access, CancellationToken ct)
    {
        if (ApproverCredential() is not { } caller)
            return Unauthorized();

        var (details, refusal) = await access.GetLinkAsync(HashUtils.Sha256Hex(token), caller, ct);
        return details is not null ? Ok(details) : this.TeamAccessProblem(refusal ?? TeamAccessRefusal.NotFound);
    }

    /// <summary>
    /// Gives the link's team access to a site the caller owns, with the chosen roles, limit and end:
    /// never wider than the link suggests, than the roles the caller could grant, or than the caller's
    /// own access to the site.
    /// </summary>
    [HttpPost("access-links/{token}/approve")]
    [EnableRateLimiting("invite-lookup")]
    [RemoteCommand(Invalidates = ["GetAccessLinkInfo", "MemberInvite_GetTeamAccess"])]
    [ProducesResponseType(typeof(TeamAccessGiven), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApproveAccessLink(
        [CredentialRouteValue] string token,
        [FromBody] ApproveTeamAccessRequest request,
        [FromServices] TeamAccessService access,
        CancellationToken ct)
    {
        if (await HttpContext.IsCallerSubjectInactiveAsync(ct))
            return Challenge();

        if (ApproverCredential() is not { } caller)
            return Unauthorized();

        var grant = new TeamAccessGrant(
            request.TenantId,
            request.RoleIds,
            request.LimitTo24Hours,
            AccessEndRequest.Instant(request.AccessEndsAt),
            request.TeamManagesStaff ?? true);
        var approval = await access.ApproveAsync(HashUtils.Sha256Hex(token), caller, grant, ct);
        return approval.Refusal is { } refusal
            ? this.TeamAccessProblem(refusal)
            : Ok(new TeamAccessGiven(approval.AccessId!.Value, request.TenantId));
    }

    /// <summary>
    /// Deletes the team, its managers, its invites and links, and its access to sites. Allowed when
    /// <see cref="TeamDetail.CanDelete"/> is.
    /// </summary>
    [HttpDelete("{teamId:guid}")]
    [RemoteCommand(Invalidates = ["GetMyTeams"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTeam(Guid teamId, [FromServices] TeamAccessService access, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return await access.DeleteTeamAsync(teamId, subjectId, ct) switch
        {
            TeamDeletion.Deleted => NoContent(),
            TeamDeletion.Refused => this.ProblemWithCode(
                TeamProblemCodes.DeleteRefused, "You cannot delete this team now.", StatusCodes.Status403Forbidden),
            _ => NotFound(),
        };
    }

    /// <summary>Whether the caller may create a team under <paramref name="policy"/>.</summary>
    internal static bool CanCreate(PlatformPolicy policy, AuthContext? caller) =>
        !policy.RestrictTeamCreationToAdmins || caller is { IsPlatformAdmin: true };

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private string LinkUrl(string path, string token) =>
        $"{Request.PublicScheme()}://{Request.Host}{path}?{TokenQueryParameter}={Uri.EscapeDataString(token)}";

    private TeamAccessApproverCredential? ApproverCredential() =>
        HttpContext.GetSubjectId() is { } subjectId && HttpContext.GetAuthContext() is { } auth
            ? new TeamAccessApproverCredential(subjectId, auth.AuthType, HttpContext.GetGrantedScopes(), auth.CredentialLimitTo24Hours)
            : null;

    private IActionResult AcceptanceResult(TeamInviteAcceptance acceptance) => acceptance switch
    {
        { Refusal: null, TeamId: { } teamId } => Ok(new TeamInviteAccepted(teamId)),
        { Refusal: TeamInviteRefusal.NotFound } => NotFound(),
        { Refusal: TeamInviteRefusal.AlreadyMember } => this.ProblemWithCode(
            TeamProblemCodes.AlreadyMember, "You are already a member of this team.", StatusCodes.Status400BadRequest),
        _ => this.ProblemWithCode(
            UnusableCode(acceptance.Refusal) ?? TeamProblemCodes.NoLongerValid,
            UnusableDetail(acceptance.Refusal),
            StatusCodes.Status400BadRequest),
    };

    /// <summary>
    /// The refusal of a staff member's role ceiling or end, or null with what they narrow to. The ceiling
    /// is a seed role for data only, or null for everything the team holds at a site. A passed end is
    /// refused here only when <paramref name="refusePassedEnd"/>; an edit leaves that to the repository,
    /// which knows the end the staff member already has.
    /// </summary>
    private ObjectResult? NarrowingProblem(
        string? roleCeilingSlug, bool limitTo24Hours, DateTimeOffset? endsAt, bool refusePassedEnd,
        out TeamStaffNarrowing? narrowing)
    {
        narrowing = null;
        if (roleCeilingSlug is not null && TeamSuggestableRoles.PermissionsOf(roleCeilingSlug) is null)
            return this.ProblemWithCode(
                TeamProblemCodes.StaffCeilingInvalid,
                "The role ceiling must be one a staff member may have.",
                StatusCodes.Status400BadRequest);

        var end = AccessEndRequest.Instant(endsAt);
        if (refusePassedEnd && MembershipLiveness.HasEnded(end, Now))
            return PassedEndProblem();

        narrowing = new TeamStaffNarrowing(roleCeilingSlug, limitTo24Hours, end);
        return null;
    }

    private ObjectResult PassedEndProblem() => this.ProblemWithCode(
        TeamProblemCodes.StaffEndInvalid, "The end must be in the future.", StatusCodes.Status400BadRequest);

    /// <summary>The refusal of a link's expiry, number of uses or label, or null with the trimmed label.</summary>
    private ObjectResult? LinkProblem(int expiresInDays, int? maxUses, string? requestedLabel, out string? label)
    {
        label = string.IsNullOrWhiteSpace(requestedLabel) ? null : requestedLabel.Trim();

        if (expiresInDays is < 1 or > TenantCreationInviteService.MaxExpiresInDays)
            return this.ProblemWithCode(
                TeamProblemCodes.ExpiryInvalid,
                $"Expiry must be between 1 and {TenantCreationInviteService.MaxExpiresInDays} days.",
                StatusCodes.Status400BadRequest);

        if (maxUses is < 1 or > TenantCreationInviteService.MaxUsesCeiling)
            return this.ProblemWithCode(
                TeamProblemCodes.UsesInvalid,
                $"The number of uses must be between 1 and {TenantCreationInviteService.MaxUsesCeiling}.",
                StatusCodes.Status400BadRequest);

        if (label is { Length: > MaxLabelLength })
            return this.ProblemWithCode(
                TeamProblemCodes.LabelInvalid,
                $"The label must be {MaxLabelLength} characters or fewer.",
                StatusCodes.Status400BadRequest);

        return null;
    }

    private static string? TeamName(string? name) =>
        name?.Trim() is { Length: > 0 and <= TeamEntity.NameMaxLength } trimmed ? trimmed : null;

    private ObjectResult NameProblem() => this.ProblemWithCode(
        TeamProblemCodes.NameInvalid,
        $"The team name must be 1 to {TeamEntity.NameMaxLength} characters.",
        StatusCodes.Status400BadRequest);

    private static string? UnusableCode(TeamInviteRefusal? refusal) => refusal switch
    {
        TeamInviteRefusal.Expired => TeamProblemCodes.Expired,
        TeamInviteRefusal.Revoked => TeamProblemCodes.Revoked,
        TeamInviteRefusal.UsedUp => TeamProblemCodes.UsedUp,
        TeamInviteRefusal.NoLongerValid => TeamProblemCodes.NoLongerValid,
        TeamInviteRefusal.StaffEndPassed => TeamProblemCodes.StaffInviteEndPassed,
        _ => null,
    };

    private static string? UnusableDetail(TeamInviteRefusal? refusal) => refusal switch
    {
        TeamInviteRefusal.Expired => "This invite has expired.",
        TeamInviteRefusal.Revoked => "This invite has been revoked.",
        TeamInviteRefusal.UsedUp => "This invite has reached its maximum uses.",
        TeamInviteRefusal.NoLongerValid => "This invite is no longer valid.",
        TeamInviteRefusal.StaffEndPassed => "The end this invite gives has passed.",
        _ => null,
    };
}

/// <summary>The caller's teams, and whether they may create one.</summary>
public sealed record MyTeamsResponse(IReadOnlyList<TeamSummary> Items, bool CanCreate);

public sealed record TeamNameRequest([property: MinLength(1), MaxLength(TeamEntity.NameMaxLength)] string Name);

/// <param name="MaxUses">How many people the link may add; null for no cap.</param>
public sealed record CreateTeamInviteRequest(
    [property: MaxLength(TeamsController.MaxLabelLength)] string? Label, int ExpiresInDays = 7, int? MaxUses = 1);

/// <summary>A new manager invite. <paramref name="Token"/> is never returned again.</summary>
public sealed record TeamInviteCreated(Guid Id, string Token, string InviteUrl, DateTime ExpiresAt);

/// <summary>
/// A staff invite to make. <paramref name="RoleCeilingSlug"/> is a seed role for data only
/// (<see cref="Scope.IsDataOnly"/>) that the staff member reaches no more than, null for everything the team
/// holds at a site; <paramref name="LimitTo24Hours"/> limits them to the last 24 hours;
/// <paramref name="StaffEndsAt"/> is when their place ends, null for no end; and
/// <paramref name="MaxUses"/> is how many people the link may add, null for no cap.
/// </summary>
public sealed record CreateTeamStaffInviteRequest(
    [property: MaxLength(TeamsController.MaxLabelLength)] string? Label,
    string? RoleCeilingSlug,
    bool LimitTo24Hours,
    DateTimeOffset? StaffEndsAt,
    int ExpiresInDays = 7,
    int? MaxUses = 1);

/// <summary>A new staff invite. <paramref name="Token"/> is never returned again.</summary>
public sealed record TeamStaffInviteCreated(Guid Id, string Token, string InviteUrl, DateTime ExpiresAt);

/// <summary>What narrows a staff member: see <see cref="CreateTeamStaffInviteRequest"/>. <paramref name="EndsAt"/> null means no end.</summary>
public sealed record UpdateTeamStaffRequest(string? RoleCeilingSlug, bool LimitTo24Hours, DateTimeOffset? EndsAt);

/// <summary>
/// An access link to make. <paramref name="RoleSlug"/> is the suggested role, a seed role for data
/// only (<see cref="Scope.IsDataOnly"/>); <paramref name="LimitTo24Hours"/> makes the 24-hour limit
/// required; <paramref name="AccessEndsAt"/> is the latest the access may end, null for no end; and
/// <paramref name="MaxUses"/> is how many sites the link may be used for, null for no cap.
/// </summary>
public sealed record CreateTeamAccessLinkRequest(
    [property: MaxLength(TeamsController.MaxLabelLength)] string? Label,
    [property: Required] string RoleSlug,
    bool LimitTo24Hours,
    DateTimeOffset? AccessEndsAt,
    int ExpiresInDays = 7,
    int? MaxUses = 1);

/// <summary>A new access link. <paramref name="Token"/> is never returned again.</summary>
public sealed record TeamAccessLinkCreated(Guid Id, string Token, string LinkUrl, DateTime ExpiresAt);

/// <summary>
/// An approval: <paramref name="TenantId"/> is a site the caller owns, <paramref name="RoleIds"/> are at
/// least one of its roles, and <paramref name="AccessEndsAt"/> is when the access ends, null for no end.
/// <paramref name="TeamManagesStaff"/> says the team's managers decide which of their staff reach the
/// site; absent means true, and false makes each staff member wait for the site to approve them.
/// </summary>
public sealed record ApproveTeamAccessRequest(
    Guid TenantId,
    [property: Required] IReadOnlyList<Guid> RoleIds,
    bool LimitTo24Hours,
    DateTimeOffset? AccessEndsAt,
    bool? TeamManagesStaff);

/// <summary>The access an approval made.</summary>
public sealed record TeamAccessGiven(Guid AccessId, Guid TenantId);

/// <param name="AlreadyMember">Whether the caller is already a member, so accepting changes nothing.</param>
/// <param name="Kind">What accepting makes the caller, a <see cref="TeamMemberKind"/> value.</param>
public sealed record TeamInviteDetails(
    Guid TeamId, string TeamName, string CreatedByName, string Kind, bool AlreadyMember);

/// <param name="TeamId">The team the invite is for.</param>
/// <param name="TeamName">The team's name.</param>
/// <param name="CreatedByName">The manager who made the invite.</param>
/// <param name="Narrowing">What narrows the staff member accepting makes the caller.</param>
/// <param name="AlreadyMember">Whether the caller is already a member of either kind, so accepting changes nothing.</param>
/// <param name="SuggestableRoles">The roles a role ceiling may name, so the ceiling can be shown by its name.</param>
public sealed record TeamStaffInviteDetails(
    Guid TeamId,
    string TeamName,
    string CreatedByName,
    TeamStaffNarrowing Narrowing,
    bool AlreadyMember,
    IReadOnlyList<TeamSuggestableRole> SuggestableRoles);

/// <summary>
/// The status and code of each reason a team's access cannot be given, or changed, as an owner sets it.
/// </summary>
public static class TeamAccessProblems
{
    public static IActionResult TeamAccessProblem(this ControllerBase controller, TeamAccessRefusal refusal) => refusal switch
    {
        TeamAccessRefusal.NotFound => controller.NotFound(),
        TeamAccessRefusal.Expired => controller.ProblemWithCode(
            TeamProblemCodes.Expired, "This link has expired.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.Revoked => controller.ProblemWithCode(
            TeamProblemCodes.Revoked, "This link has been revoked.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.UsedUp => controller.ProblemWithCode(
            TeamProblemCodes.UsedUp, "This link has reached its maximum uses.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.NoLongerValid => controller.ProblemWithCode(
            TeamProblemCodes.NoLongerValid, "This link is no longer valid.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.AlreadyGiven => controller.ProblemWithCode(
            TeamProblemCodes.AccessAlreadyGiven, "This team already has access to this site.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.NotOwner => controller.ProblemWithCode(
            TeamProblemCodes.AccessNotOwner, "Only an owner of the site can give a team access to it or change that access.", StatusCodes.Status403Forbidden),
        TeamAccessRefusal.NoRoles => controller.ProblemWithCode(
            TeamProblemCodes.AccessRolesRequired, "Choose at least one role.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.RoleNotInTenant => controller.ProblemWithCode(
            TeamProblemCodes.AccessRoleNotInSite, "A role is not one of this site's roles.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.RoleAdministrative => controller.ProblemWithCode(
            TeamProblemCodes.AccessRoleAdministrative, "A team can be given only roles for data.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.RoleWiderThanLink => controller.ProblemWithCode(
            TeamProblemCodes.AccessRoleWiderThanLink, "A role gives more than this link allows.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.RoleWiderThanApprover => controller.ProblemWithCode(
            TeamProblemCodes.AccessRoleWiderThanApprover, "A role gives more than you hold.", StatusCodes.Status403Forbidden),
        TeamAccessRefusal.LimitRequired => controller.ProblemWithCode(
            TeamProblemCodes.AccessLimitRequired, "The access must be limited to the last 24 hours.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.EndLaterThanLink => controller.ProblemWithCode(
            TeamProblemCodes.AccessEndLaterThanLink, "The access must end no later than this link allows.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.EndOutlastsApprover => controller.ProblemWithCode(
            TeamProblemCodes.AccessEndOutlastsApprover, HttpContextExtensions.AccessEndCeilingDetail, StatusCodes.Status403Forbidden),
        TeamAccessRefusal.EndInPast => controller.ProblemWithCode(
            TeamProblemCodes.AccessEndInvalid, "The end must be in the future.", StatusCodes.Status400BadRequest),
        TeamAccessRefusal.SuggestedEndPassed => controller.ProblemWithCode(
            TeamProblemCodes.AccessLinkEndPassed, "The latest end this link allows has passed.", StatusCodes.Status400BadRequest),
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null),
    };
}

/// <summary>The problem codes the team routes answer with, which the web app words itself.</summary>
public static class TeamProblemCodes
{
    public const string NameInvalid = "team_name_invalid";
    public const string LabelInvalid = "team_invite_label_invalid";
    public const string ExpiryInvalid = "team_invite_expiry_invalid";
    public const string UsesInvalid = "team_invite_uses_invalid";
    public const string LastManager = "team_last_manager";
    public const string CreationRestricted = "team_creation_restricted";
    public const string DeleteRefused = "team_delete_refused";
    public const string Expired = "team_invite_expired";
    public const string Revoked = "team_invite_revoked";
    public const string UsedUp = "team_invite_used_up";
    public const string NoLongerValid = "team_invite_no_longer_valid";
    public const string AlreadyMember = "team_invite_already_member";
    public const string AccessRoleInvalid = "team_access_role_invalid";
    public const string AccessEndInvalid = "team_access_end_invalid";
    public const string AccessAlreadyGiven = "team_access_already_given";
    public const string AccessNotOwner = "team_access_not_owner";
    public const string AccessRolesRequired = "team_access_roles_required";
    public const string AccessRoleNotInSite = "team_access_role_not_in_site";
    public const string AccessRoleAdministrative = "team_access_role_administrative";
    public const string AccessRoleWiderThanLink = "team_access_role_wider_than_link";
    public const string AccessRoleWiderThanApprover = "team_access_role_wider_than_approver";
    public const string AccessLimitRequired = "team_access_limit_required";
    public const string AccessEndLaterThanLink = "team_access_end_later_than_link";
    public const string AccessEndOutlastsApprover = "team_access_end_outlasts_approver";
    public const string AccessLinkEndPassed = "team_access_link_end_passed";
    public const string StaffCeilingInvalid = "team_staff_ceiling_invalid";
    public const string StaffEndInvalid = "team_staff_end_invalid";
    public const string StaffInviteEndPassed = "team_staff_invite_end_passed";
    public const string StaffDecisionNotOwner = "team_staff_decision_not_owner";
}

public sealed record TeamInviteAccepted(Guid TeamId);
