using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.CoachMarks;
using Nocturne.Core.Models.CoachMarks;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Whether the signed-in account should be reminded to add a recovery email. A caller can read
/// the answer for its own account.
/// </summary>
/// <remarks>
/// Never shown to platform-access operators or service credentials (<see cref="OwnAccountCaller"/>),
/// nor where this site cannot send the email that confirms an address.
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/me/recovery-email-prompt")]
[ErrorEnvelope]
[Authorize]
[Produces("application/json")]
public class RecoveryEmailPromptController(
    ISubjectRepository subjects,
    ICoachMarkService coachMarks,
    IAccountLinkRoute linkRoute,
    TimeProvider time) : ControllerBase
{
    /// <summary>How long a dismissal hides the reminder.</summary>
    public static readonly TimeSpan DismissalLasts = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether the caller has no confirmed recovery email and has not dismissed the reminder in the last 30 days.
    /// </summary>
    /// <remarks>
    /// The dismissal is per member per site, but the address is the account's, so confirming it once
    /// silences the reminder on every site.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(RecoveryEmailPromptResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RecoveryEmailPromptResponse>> GetRecoveryEmailPrompt(CancellationToken ct)
    {
        if (OwnAccountCaller.SubjectId(HttpContext) is not { } subjectId || !linkRoute.IsConfigured)
            return Ok(new RecoveryEmailPromptResponse());

        var subject = await subjects.GetAsync(subjectId, ct);
        if (subject is null or { IsDemoSubject: true } or { EmailConfirmedAt: not null })
            return Ok(new RecoveryEmailPromptResponse());

        var mark = await coachMarks.GetAsync(CoachMarkKeys.RecoveryEmailPrompt, ct);
        var dismissed = mark is { Status: "dismissed", CompletedAt: { } at }
            && at > time.GetUtcNow().UtcDateTime - DismissalLasts;
        return Ok(new RecoveryEmailPromptResponse { Show = !dismissed, HasAddress = !string.IsNullOrWhiteSpace(subject.Email) });
    }
}

/// <summary>Whether to remind the caller to add a recovery email.</summary>
public class RecoveryEmailPromptResponse
{
    /// <summary>True when the caller has no confirmed recovery email and the reminder is not dismissed.</summary>
    public bool Show { get; set; }

    /// <summary>True when the caller holds an address that is not yet confirmed, so the reminder asks to confirm it rather than to add one.</summary>
    public bool HasAddress { get; set; }
}
