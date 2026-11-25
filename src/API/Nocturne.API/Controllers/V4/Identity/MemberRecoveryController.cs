using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
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
/// Restores access for members who have no sign-in method left. A member administrator can issue
/// each such member a single-use code that the member uses to regain access.
/// </summary>
/// <remarks>
/// Allowed during setup because a site where these members are stuck and no member administrator
/// can sign in is in recovery mode, where every other tenant endpoint answers 503. On a site an
/// administrator can still reach, the same endpoints serve the administrator who recovers them.
/// The member spends the code at <see cref="PasskeyController.RecoveryModeRedeem"/>.
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
    private readonly ITenantMembershipRepository _members;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantRoleService _tenantRoleService;
    private readonly MemberRecoveryEligibility _eligibility;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly ILoginCodeService _loginCodeService;

    public MemberRecoveryController(
        INocturneDbContextFactory contexts,
        ITenantMembershipRepository members,
        ITenantAccessor tenantAccessor,
        ITenantRoleService tenantRoleService,
        MemberRecoveryEligibility eligibility,
        IRecoveryCodeService recoveryCodeService,
        ILoginCodeService loginCodeService)
    {
        _contexts = contexts;
        _members = members;
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
    [RequireScope(Scope.MembersManage)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<LockedOutMember>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetLockedOutMembers(CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var subjects = await _eligibility.ListLockedOutAsync(db, _tenantAccessor.TenantId, ct);

        var members = new List<LockedOutMember>(subjects.Count);
        foreach (var s in subjects)
        {
            members.Add(new LockedOutMember(
                s.Id, s.Name, s.Username,
                HasRecoveryCodes: await _recoveryCodeService.GetRemainingCountAsync(s.Id) > 0,
                Refusal: await RefusalAsync(
                    _tenantAccessor.TenantId,
                    s.Id,
                    await _eligibility.IsOperatorOnlyAsync(_tenantAccessor.TenantId, s.Id, s.IsPlatformAdmin, ct),
                    ct)));
        }

        return Ok(members);
    }

    /// <summary>
    /// Issues a single-use recovery code for a member who cannot sign in. The plaintext is returned
    /// once; the member redeems it within <see cref="LoginCodeService.LifetimeOf"/> for
    /// <see cref="LoginCodePurpose.Recovery"/>.
    /// The caller must be signed in as themselves, hold every permission the member holds, and not be confined to less history than the member sees.
    /// A member's existing recovery codes do not block issuance; redeeming the new code cancels them.
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
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetLockedOutMembers"])]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(LoginCode), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> IssueRecoveryCode(Guid subjectId, CancellationToken ct)
    {
        // [RequireScope] answers 401 to a request with no auth context, as members.manage is not a read scope.
        var auth = HttpContext.GetAuthContext()!;
        if (HttpContext.CallerGrantCeiling() is null)
            return this.CallerAccessFromTeamProblem();

        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        var tenantId = _tenantAccessor.TenantId;
        await using var db = await _contexts.CreateAsync(ct);

        var eligibility = await _eligibility.CheckAsync(db, tenantId, subjectId, ct);
        if (eligibility == RecoveryEligibility.NotLockedOut)
            return NotFound();

        var refusal = await RefusalAsync(
            tenantId, subjectId, eligibility == RecoveryEligibility.OperatorOnly, ct);
        if (refusal is not null)
            return RefusalResult(refusal.Value);

        var issued = await _loginCodeService.IssueAsync(
            db, subjectId, LoginCodePurpose.Recovery,
            HttpContext.GetClientAddress(),
            Request.Headers.UserAgent.ToString(),
            AuthAuditActor.From(auth),
            ct);

        return Ok(issued);
    }

    /// <summary>
    /// Why the caller may not issue a recovery code for the member, or null when the caller may.
    /// <see cref="IssueRecoveryCode"/> refuses on exactly this and the list sends it. The one
    /// refusal outside it is <see cref="RequireInteractiveSessionAttribute"/>: a non-interactive
    /// credential that holds <see cref="Scope.MembersManage"/> lists no refusal and the issue
    /// still answers 403.
    /// </summary>
    private async Task<RecoveryIssueRefusal?> RefusalAsync(
        Guid tenantId, Guid subjectId, bool operatorOnly, CancellationToken ct)
    {
        if (operatorOnly)
            return RecoveryIssueRefusal.OperatorOnly;

        var member = await _members.FindMemberAsync(tenantId, subjectId, ct)
            ?? throw new InvalidOperationException($"Subject {subjectId} is not a member of tenant {tenantId}.");

        var memberPermissions = await _tenantRoleService.GetEffectivePermissionsAsync(member.Id, ct);
        if (HttpContext.CallerGrantCeiling() is not { } ceiling
            || Scope.ValidateGrant(memberPermissions, ceiling.Scopes) is not null)
            return RecoveryIssueRefusal.MissingPermissions;

        if (!member.LimitTo24Hours && ceiling.HistoryClamped)
            return RecoveryIssueRefusal.HistoryClamped;

        return HttpContext.OutlastsCallerAccess(member.AccessEndsAt)
            ? RecoveryIssueRefusal.AccessOutlasts
            : null;
    }

    private IActionResult RefusalResult(RecoveryIssueRefusal refusal) => refusal switch
    {
        RecoveryIssueRefusal.OperatorOnly => Problem(detail: OperatorOnlyDetail, statusCode: 409, title: "Conflict"),
        RecoveryIssueRefusal.MissingPermissions => Forbid(),
        RecoveryIssueRefusal.HistoryClamped => Problem(detail: HttpContextExtensions.HistoryCeilingDetail, statusCode: 403),
        RecoveryIssueRefusal.AccessOutlasts => Problem(detail: HttpContextExtensions.AccessEndCeilingDetail, statusCode: 403),
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null),
    };
}

/// <summary>Why a caller may not issue a member's recovery code, in the order the issue checks.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RecoveryIssueRefusal>))]
public enum RecoveryIssueRefusal
{
    /// <summary>Only the operator may issue it: the account reaches beyond the tenant.</summary>
    OperatorOnly,

    /// <summary>The member holds a permission the caller does not.</summary>
    MissingPermissions,

    /// <summary>The caller sees the last 24 hours only and the member sees more.</summary>
    HistoryClamped,

    /// <summary>The caller's access ends on a date and the member's lasts longer.</summary>
    AccessOutlasts,
}

/// <param name="SubjectId">The member's subject.</param>
/// <param name="Name">Display name.</param>
/// <param name="Username">Sign-in name, when the member has one.</param>
/// <param name="HasRecoveryCodes">Whether the member can still sign in with a recovery code of their own.</param>
/// <param name="Refusal">Why the caller may not issue this member's recovery code; null when the caller may.</param>
public record LockedOutMember(
    Guid SubjectId, string Name, string? Username, bool HasRecoveryCodes, RecoveryIssueRefusal? Refusal);
