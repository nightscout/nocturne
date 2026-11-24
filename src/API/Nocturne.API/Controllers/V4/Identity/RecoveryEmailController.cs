using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// The signed-in member's own recovery email: the address a recovery link is sent to when they
/// lose their sign-in. Only the member reads it, and only the link sent to it confirms it.
/// </summary>
/// <remarks>
/// Not offered to platform-access operators or service credentials, whose subject is not the
/// account being asked about. Where no delivery route is configured
/// (<see cref="IAccountLinkRoute.IsConfigured"/>) an address could never be confirmed, so the
/// read reports it unavailable and the write refuses.
/// </remarks>
/// <seealso cref="AccountLinkService"/>
[ApiController]
[Tags("Identity")]
[Route("api/v4/me/recovery-email")]
[ErrorEnvelope]
[Authorize]
[Produces("application/json")]
public class RecoveryEmailController(
    ISubjectRepository subjects,
    ISubjectService subjectService,
    AccountLinkService links,
    IAccountLinkRoute linkRoute) : ControllerBase
{
    /// <summary>
    /// The caller's own recovery email, whether it is confirmed, and whether this site can send the email that confirms one. Only the person's own sign-in reads it, not an app or grant acting for them.
    /// </summary>
    [HttpGet]
    [RequireInteractiveSession]
    [RemoteQuery]
    [ProducesResponseType(typeof(RecoveryEmailResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RecoveryEmailResponse>> GetRecoveryEmail(CancellationToken ct)
    {
        if (OwnAccountCaller.SubjectId(HttpContext) is not { } subjectId || !linkRoute.IsConfigured)
            return Ok(new RecoveryEmailResponse());

        var subject = await subjects.GetAsync(subjectId, ct);
        if (subject is null or { IsDemoSubject: true })
            return Ok(new RecoveryEmailResponse());

        return Ok(new RecoveryEmailResponse
        {
            Available = true,
            Email = subject.Email,
            Confirmed = subject.EmailConfirmedAt is not null,
        });
    }

    /// <summary>
    /// Sets or changes the caller's recovery email and sends the link that confirms it. The answer says whether a link was sent now. 404 when this site cannot send email. 429 when a link was sent too recently and 502 when the link could not be sent; the address is saved in both, and the problem code says which.
    /// </summary>
    /// <remarks>
    /// Setting the address the account holds, confirmed, changes nothing and sends nothing. Setting
    /// the one it holds unconfirmed sends the link again and keeps the stored spelling. A different
    /// address starts unconfirmed.
    /// </remarks>
    [HttpPut]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [EnableRateLimiting("recovery-email")]
    [RemoteCommand(Invalidates = ["GetRecoveryEmail"])]
    [ProducesResponseType(typeof(SetRecoveryEmailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SetRecoveryEmail([FromBody] SetRecoveryEmailRequest request, CancellationToken ct)
    {
        if (OwnAccountCaller.SubjectId(HttpContext) is not { } subjectId
            || HttpContext.GetTenantContext() is not { } tenant)
            return Forbid();
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();
        if (!linkRoute.IsConfigured)
            return Unavailable();

        var stored = await subjects.GetAsync(subjectId, ct);
        if (stored is null)
            return Forbid();

        var email = request.Email!.Trim();
        var same = EmailAddresses.AreSame(stored.Email, email);
        if (same && stored.EmailConfirmedAt is not null)
            return Ok(new SetRecoveryEmailResponse { Email = stored.Email, Confirmed = true, Sent = false });

        if (!same && !await ChangeEmailAsync(subjectId, email))
            return Forbid();

        var sent = await links.SendEmailConfirmationAsync(
            tenant, subjectId, AuthAuditActor.From(HttpContext.GetAuthContext()!),
            HttpContext.GetClientAddress(), Request.Headers.UserAgent.ToString(), ct);

        return sent switch
        {
            AccountLinkSend.Sent => Ok(new SetRecoveryEmailResponse { Email = same ? stored.Email : email, Confirmed = false, Sent = true }),
            AccountLinkSend.AlreadyConfirmed => Ok(new SetRecoveryEmailResponse { Email = same ? stored.Email : email, Confirmed = true, Sent = false }),
            AccountLinkSend.NotConfigured => Unavailable(),
            AccountLinkSend.Throttled => this.ProblemWithCode(
                AccountLinkProblemCodes.Throttled, "A link was sent recently.", StatusCodes.Status429TooManyRequests),
            _ => this.ProblemWithCode(
                AccountLinkProblemCodes.DeliveryFailed, "The link could not be sent.", StatusCodes.Status502BadGateway),
        };
    }

    /// <summary>
    /// Clears the caller's recovery email.
    /// </summary>
    [HttpDelete]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["GetRecoveryEmail"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteRecoveryEmail()
    {
        if (OwnAccountCaller.SubjectId(HttpContext) is not { } subjectId)
            return Forbid();

        return await ChangeEmailAsync(subjectId, null) ? NoContent() : Forbid();
    }

    private async Task<bool> ChangeEmailAsync(Guid subjectId, string? email)
    {
        var subject = await subjectService.GetSubjectByIdAsync(subjectId);
        if (subject is null)
            return false;

        subject.Email = email;
        return await subjectService.UpdateSubjectAsync(subject) is not null;
    }

    private ObjectResult Unavailable() => this.ProblemWithCode(
        AccountLinkProblemCodes.Unavailable, "This instance cannot send email.", StatusCodes.Status404NotFound);
}

/// <summary>The caller's recovery email.</summary>
public class RecoveryEmailResponse
{
    /// <summary>
    /// Whether this site can send the email that confirms a recovery email. When false nothing
    /// about recovery email is shown or asked for, and <see cref="Email"/> is empty.
    /// </summary>
    public bool Available { get; set; }

    /// <summary>The address, or null when the caller has none.</summary>
    public string? Email { get; set; }

    /// <summary>Whether the link sent to the address was followed.</summary>
    public bool Confirmed { get; set; }
}

/// <summary>What setting the caller's recovery email did.</summary>
public class SetRecoveryEmailResponse
{
    /// <summary>The address the account holds now.</summary>
    public string? Email { get; set; }

    /// <summary>Whether that address is confirmed.</summary>
    public bool Confirmed { get; set; }

    /// <summary>Whether a confirmation link was sent by this request.</summary>
    public bool Sent { get; set; }
}

/// <summary>The address to set as the caller's recovery email.</summary>
public class SetRecoveryEmailRequest
{
    /// <summary>One email address and nothing else.</summary>
    [Required]
    [BareEmailAddress]
    [StringLength(255)]
    public string? Email { get; set; }
}
