using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Lets a member administrator restore access for a member who has no sign-in method left, by
/// issuing a single-use code the member spends at <see cref="PasskeyController.RecoveryModeRedeem"/>.
/// </summary>
/// <remarks>
/// Allowed during setup because these members are what put the tenant into recovery mode, where
/// every other tenant endpoint answers 503.
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/member-recovery")]
[Produces("application/json")]
[Authorize]
[AllowDuringSetup]
public class MemberRecoveryController : ControllerBase
{
    /// <summary>
    /// The credentials that are the person rather than a delegation they handed out, as in
    /// <see cref="PlatformAdminCredentials"/>. A recovery code puts a passkey on someone else's
    /// account, so a token or api-secret holding <see cref="Scope.MembersManage"/> must not mint one.
    /// </summary>
    private static readonly IReadOnlySet<AuthType> InteractiveCredentials = new HashSet<AuthType>
    {
        AuthType.SessionCookie,
        AuthType.OidcToken,
        AuthType.LegacyJwt,
    };

    private readonly INocturneDbContextFactory _contexts;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantRoleService _tenantRoleService;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly ILoginCodeService _loginCodeService;

    public MemberRecoveryController(
        INocturneDbContextFactory contexts,
        ITenantAccessor tenantAccessor,
        ITenantRoleService tenantRoleService,
        IRecoveryCodeService recoveryCodeService,
        ILoginCodeService loginCodeService)
    {
        _contexts = contexts;
        _tenantAccessor = tenantAccessor;
        _tenantRoleService = tenantRoleService;
        _recoveryCodeService = recoveryCodeService;
        _loginCodeService = loginCodeService;
    }

    /// <summary>
    /// The members of this tenant who cannot sign in (<see cref="OrphanedSubjectFilter"/>).
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<LockedOutMember>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetLockedOutMembers(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.MembersManage))
            return Forbid();

        await using var db = await _contexts.CreateAsync(ct);
        var subjects = await db.OrphanedSubjectsOf(_tenantAccessor.TenantId)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Username })
            .ToListAsync(ct);

        var members = new List<LockedOutMember>(subjects.Count);
        foreach (var s in subjects)
        {
            members.Add(new LockedOutMember(
                s.Id, s.Name, s.Username,
                await _recoveryCodeService.GetRemainingCountAsync(s.Id) > 0));
        }

        return Ok(members);
    }

    /// <summary>
    /// Issues a single-use recovery code for a member who cannot sign in. The plaintext is returned
    /// once; the member redeems it within <see cref="LoginCodeService.Lifetime"/>.
    /// </summary>
    /// <remarks>
    /// The code lets its holder put a passkey on the member's account, so the caller must be signed
    /// in as themselves, hold every permission the member holds, and not be confined to less
    /// history than the member sees. A member who still has recovery codes is refused: those are
    /// their own way back in, and nobody else needs to hold one for them.
    /// </remarks>
    [HttpPost("{subjectId:guid}/code")]
    [RemoteCommand(Invalidates = ["GetLockedOutMembers"])]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(LoginCode), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> IssueRecoveryCode(Guid subjectId, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth is null || !InteractiveCredentials.Contains(auth.AuthType)
            || !HttpContext.HasScope(Scope.MembersManage))
            return Forbid();

        var tenantId = _tenantAccessor.TenantId;
        await using var db = await _contexts.CreateAsync(ct);

        var member = await db.TenantMembers
            .Where(m => m.TenantId == tenantId && m.SubjectId == subjectId
                && db.OrphanedSubjectsOf(tenantId).Any(s => s.Id == subjectId))
            .Select(m => new { m.Id, m.LimitTo24Hours })
            .FirstOrDefaultAsync(ct);
        if (member is null)
            return NotFound();

        var memberPermissions = await _tenantRoleService.GetEffectivePermissionsAsync(member.Id, ct);
        if (Scope.ValidateGrant(memberPermissions, HttpContext.GetGrantedScopes()) is not null)
            return Forbid();

        if (!member.LimitTo24Hours && HttpContext.IsCallerHistoryClamped())
            return Problem(detail: HttpContextExtensions.HistoryCeilingDetail, statusCode: 403);

        if (await _recoveryCodeService.GetRemainingCountAsync(subjectId) > 0)
            return Problem(
                detail: "This member still has recovery codes. They can sign in with one of those.",
                statusCode: 409,
                title: "Conflict");

        var issued = await _loginCodeService.IssueAsync(
            db, subjectId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            AuthAuditActor.From(auth),
            ct);

        return Ok(issued);
    }
}

/// <param name="SubjectId">The member's subject.</param>
/// <param name="Name">Display name.</param>
/// <param name="Username">Sign-in name, when the member has one.</param>
/// <param name="HasRecoveryCodes">Whether the member can still sign in with a recovery code of their own.</param>
public record LockedOutMember(Guid SubjectId, string Name, string? Username, bool HasRecoveryCodes);
