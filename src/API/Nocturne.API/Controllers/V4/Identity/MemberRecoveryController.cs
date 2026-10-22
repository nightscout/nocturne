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
using Nocturne.Infrastructure.Data.Entities;
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
    private const string OperatorOnlyDetail =
        "This account is an instance administrator or also belongs to another space, so only the operator can issue its recovery code.";

    private readonly INocturneDbContextFactory _contexts;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantRoleService _tenantRoleService;
    private readonly MemberRecoveryEligibility _eligibility;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly ILoginCodeService _loginCodeService;

    public MemberRecoveryController(
        INocturneDbContextFactory contexts,
        ITenantAccessor tenantAccessor,
        ITenantRoleService tenantRoleService,
        MemberRecoveryEligibility eligibility,
        IRecoveryCodeService recoveryCodeService,
        ILoginCodeService loginCodeService)
    {
        _contexts = contexts;
        _tenantAccessor = tenantAccessor;
        _tenantRoleService = tenantRoleService;
        _eligibility = eligibility;
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
            .Select(s => new { s.Id, s.Name, s.Username, s.IsPlatformAdmin })
            .ToListAsync(ct);

        var members = new List<LockedOutMember>(subjects.Count);
        foreach (var s in subjects)
        {
            members.Add(new LockedOutMember(
                s.Id, s.Name, s.Username,
                HasRecoveryCodes: await _recoveryCodeService.GetRemainingCountAsync(s.Id) > 0,
                OperatorOnly: await _eligibility.IsOperatorOnlyAsync(
                    _tenantAccessor.TenantId, s.Id, s.IsPlatformAdmin, ct)));
        }

        return Ok(members);
    }

    /// <summary>
    /// Issues a single-use recovery code for a member who cannot sign in. The plaintext is returned
    /// once; the member redeems it within <see cref="LoginCodeService.LifetimeOf"/> for
    /// <see cref="LoginCodePurpose.Recovery"/>.
    /// </summary>
    /// <remarks>
    /// The code lets its holder put a passkey on the member's account, so the caller must be signed
    /// in as themselves, hold every permission the member holds, and not be confined to less
    /// history than the member sees. Who is eligible at all is <see cref="MemberRecoveryEligibility"/>.
    /// <para>
    /// A member who still holds recovery codes may have lost them, so those do not block issuance;
    /// redeeming this code cancels them (<see cref="PasskeyController.RecoveryModeRedeem"/>).
    /// </para>
    /// </remarks>
    [HttpPost("{subjectId:guid}/code")]
    [RequireInteractiveSession]
    [RemoteCommand(Invalidates = ["GetLockedOutMembers"])]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(LoginCode), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> IssueRecoveryCode(Guid subjectId, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth is null || !HttpContext.HasScope(Scope.MembersManage))
            return Forbid();

        var tenantId = _tenantAccessor.TenantId;
        await using var db = await _contexts.CreateAsync(ct);

        switch (await _eligibility.CheckAsync(db, tenantId, subjectId, ct))
        {
            case RecoveryEligibility.NotLockedOut:
                return NotFound();
            case RecoveryEligibility.OperatorOnly:
                return Problem(detail: OperatorOnlyDetail, statusCode: 409, title: "Conflict");
        }

        var member = await db.TenantMembers
            .Where(m => m.TenantId == tenantId && m.SubjectId == subjectId)
            .Select(m => new { m.Id, m.LimitTo24Hours })
            .FirstAsync(ct);

        var memberPermissions = await _tenantRoleService.GetEffectivePermissionsAsync(member.Id, ct);
        if (Scope.ValidateGrant(memberPermissions, HttpContext.GetGrantedScopes()) is not null)
            return Forbid();

        if (!member.LimitTo24Hours && HttpContext.IsCallerHistoryClamped())
            return Problem(detail: HttpContextExtensions.HistoryCeilingDetail, statusCode: 403);

        var issued = await _loginCodeService.IssueAsync(
            db, subjectId, LoginCodePurpose.Recovery,
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
/// <param name="OperatorOnly">Whether only the operator can issue this member's recovery code.</param>
public record LockedOutMember(
    Guid SubjectId, string Name, string? Username, bool HasRecoveryCodes, bool OperatorOnly);
