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
/// adding managers by invite link, removing them, and deleting it.
/// </summary>
/// <remarks>
/// Run by a person in a browser session, never by an API key or a device token
/// (<see cref="RequireInteractiveSessionAttribute"/>). A team belongs to no tenant, so these routes are
/// served on every host. A caller who is not a member of a team gets 404 on every route that names it,
/// so a team's existence is never revealed to someone outside it.
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

    private const int MaxLabelLength = 255;

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
    [LimitTeamCreationPerPerson]
    [RemoteCommand(Invalidates = ["GetMyTeams"])]
    [ProducesResponseType(typeof(TeamSummary), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateTeam([FromBody] TeamNameRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        if (!CanCreate(await policies.GetAsync(ct), HttpContext.GetAuthContext()))
            return Problem(detail: CreationRestrictedDetail, statusCode: StatusCodes.Status403Forbidden);

        if (TeamName(request.Name) is not { } name)
            return NameProblem();

        var team = await teams.CreateAsync(subjectId, name, ct);
        return StatusCode(StatusCodes.Status201Created, team);
    }

    /// <summary>
    /// The team's name, managers and live manager invites, and what the caller may do to it.
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

    /// <summary>Renames the team.</summary>
    [HttpPut("{teamId:guid}/name")]
    [RemoteCommand(Invalidates = ["GetMyTeams", "GetTeam"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RenameTeam(Guid teamId, [FromBody] TeamNameRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        if (TeamName(request.Name) is not { } name)
            return NameProblem();

        return await teams.RenameAsync(teamId, subjectId, name, ct) ? NoContent() : NotFound();
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
            ManagerGuardedChange.LastManager => Problem(
                detail: TeamManagerFilter.LastManagerRefusal, statusCode: StatusCodes.Status400BadRequest),
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
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        if (request.ExpiresInDays is < 1 or > TenantCreationInviteService.MaxExpiresInDays)
            return Problem(
                detail: $"Expiry must be between 1 and {TenantCreationInviteService.MaxExpiresInDays} days.",
                statusCode: StatusCodes.Status400BadRequest);

        if (request.MaxUses is < 1 or > TenantCreationInviteService.MaxUsesCeiling)
            return Problem(
                detail: $"The number of uses must be between 1 and {TenantCreationInviteService.MaxUsesCeiling}.",
                statusCode: StatusCodes.Status400BadRequest);

        var label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        if (label is { Length: > MaxLabelLength })
            return Problem(
                detail: $"The label must be {MaxLabelLength} characters or fewer.",
                statusCode: StatusCodes.Status400BadRequest);

        var token = jwtService.GenerateRefreshToken();
        var expiresAt = Now.AddDays(request.ExpiresInDays);
        var inviteId = await teams.CreateInviteAsync(
            teamId, subjectId, new NewTeamInvite(HashUtils.Sha256Hex(token), label, expiresAt, request.MaxUses), ct);
        if (inviteId is not { } id)
            return NotFound();

        var url = $"{Request.PublicScheme()}://{Request.Host}{JoinPath}?{TokenQueryParameter}={Uri.EscapeDataString(token)}";
        return StatusCode(StatusCodes.Status201Created, new TeamInviteCreated(id, token, url, expiresAt));
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
            return Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest);

        return Ok(new TeamInviteDetails(
            info.TeamId, info.TeamName, info.CreatedByName, info.Refusal == TeamInviteRefusal.AlreadyMember));
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

        var acceptance = await teams.AcceptInviteAsync(HashUtils.Sha256Hex(token), subjectId, Now, ct);
        return acceptance switch
        {
            { Refusal: null, TeamId: { } teamId } => Ok(new TeamInviteAccepted(teamId)),
            { Refusal: TeamInviteRefusal.NotFound } => NotFound(),
            { Refusal: TeamInviteRefusal.AlreadyMember } => Problem(
                detail: "You are already a member of this team.", statusCode: StatusCodes.Status400BadRequest),
            _ => Problem(detail: UnusableDetail(acceptance.Refusal), statusCode: StatusCodes.Status400BadRequest),
        };
    }

    /// <summary>Deletes the team, its managers and its invites. Allowed when <see cref="TeamDetail.CanDelete"/> is.</summary>
    [HttpDelete("{teamId:guid}")]
    [RemoteCommand(Invalidates = ["GetMyTeams"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTeam(Guid teamId, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        return await teams.DeleteAsync(teamId, subjectId, ct) switch
        {
            TeamDeletion.Deleted => NoContent(),
            TeamDeletion.Refused => Problem(
                detail: "You cannot delete this team now.", statusCode: StatusCodes.Status403Forbidden),
            _ => NotFound(),
        };
    }

    /// <summary>Whether the caller may create a team under <paramref name="policy"/>.</summary>
    internal static bool CanCreate(PlatformPolicy policy, AuthContext? caller) =>
        !policy.RestrictTeamCreationToAdmins || caller is { IsPlatformAdmin: true };

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static string? TeamName(string? name) =>
        name?.Trim() is { Length: > 0 and <= TeamEntity.NameMaxLength } trimmed ? trimmed : null;

    private ObjectResult NameProblem() => Problem(
        detail: $"The team name must be 1 to {TeamEntity.NameMaxLength} characters.",
        statusCode: StatusCodes.Status400BadRequest);

    private static string? UnusableDetail(TeamInviteRefusal? refusal) => refusal switch
    {
        TeamInviteRefusal.Expired => "This invite has expired.",
        TeamInviteRefusal.Revoked => "This invite has been revoked.",
        TeamInviteRefusal.UsedUp => "This invite has reached its maximum uses.",
        TeamInviteRefusal.NoLongerValid => "This invite is no longer valid.",
        _ => null,
    };
}

/// <summary>The caller's teams, and whether they may create one.</summary>
public sealed record MyTeamsResponse(IReadOnlyList<TeamSummary> Items, bool CanCreate);

public sealed record TeamNameRequest(string Name);

/// <param name="MaxUses">How many people the link may add; null for no cap.</param>
public sealed record CreateTeamInviteRequest(string? Label, int ExpiresInDays = 7, int? MaxUses = 1);

/// <summary>A new manager invite. <paramref name="Token"/> is never returned again.</summary>
public sealed record TeamInviteCreated(Guid Id, string Token, string InviteUrl, DateTime ExpiresAt);

/// <param name="AlreadyMember">Whether the caller is already a member, so accepting changes nothing.</param>
public sealed record TeamInviteDetails(Guid TeamId, string TeamName, string CreatedByName, bool AlreadyMember);

public sealed record TeamInviteAccepted(Guid TeamId);
