using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// Confirming an account's email address by a link sent to it. A signed-in member can ask for the
/// link for their own address, and anyone holding the link can redeem it. Redeeming confirms the
/// address the link was sent to and nothing else; it does not sign anyone in.
/// </summary>
/// <seealso cref="AccountLinkService"/>
[ApiController]
[Route("api/auth/email-confirmation")]
[Tags("Authentication")]
[ErrorEnvelope]
[Produces("application/json")]
public class EmailConfirmationController(
    ISubjectRepository subjects,
    INocturneDbContextFactory contexts,
    ILoginCodeService loginCodes) : ControllerBase
{
    /// <summary>
    /// Sends a link that confirms the signed-in account's current email address. 404 when this instance has no way to send email, 409 when the account has no address or it is already confirmed, 429 when a link was sent too recently.
    /// </summary>
    [HttpPost("send")]
    [Authorize]
    [RequireInteractiveSession]
    [DenyDemoSubject]
    [EnableRateLimiting("email-confirmation")]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Send([FromServices] AccountLinkService links, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth?.SubjectId is not { } subjectId || HttpContext.GetTenantContext() is not { } tenant)
            return Problem(detail: "Authentication required", statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        var sent = await links.SendEmailConfirmationAsync(
            tenant, subjectId, AuthAuditActor.From(auth),
            HttpContext.GetClientAddress(), Request.Headers.UserAgent.ToString(), ct);

        return sent switch
        {
            AccountLinkSend.Sent => NoContent(),
            AccountLinkSend.NotConfigured => this.ProblemWithCode(
                AccountLinkProblemCodes.Unavailable, "This instance cannot send email.", StatusCodes.Status404NotFound),
            AccountLinkSend.NoAddress => this.ProblemWithCode(
                AccountLinkProblemCodes.NoAddress, "The account has no email address.", StatusCodes.Status409Conflict),
            AccountLinkSend.AlreadyConfirmed => this.ProblemWithCode(
                AccountLinkProblemCodes.AlreadyConfirmed, "The email address is already confirmed.", StatusCodes.Status409Conflict),
            AccountLinkSend.Throttled => this.ProblemWithCode(
                AccountLinkProblemCodes.Throttled, "A link was sent recently.", StatusCodes.Status429TooManyRequests),
            _ => this.ProblemWithCode(
                AccountLinkProblemCodes.DeliveryFailed, "The link could not be sent.", StatusCodes.Status502BadGateway),
        };
    }

    /// <summary>
    /// Redeems a confirmation link: confirms the address the link was sent to, if the account still holds it. Unknown, spent, expired, other-site links, and links for an address the account has since left, all fail alike.
    /// </summary>
    /// <remarks>
    /// The code proves control of one address, so it confirms that address only, through
    /// <see cref="ISubjectRepository.ConfirmEmailAsync"/>, which refuses an address the account no
    /// longer holds even when it changed while this runs; that refusal rolls the claim back. The
    /// answer says nothing about the account.
    /// </remarks>
    [HttpPost("redeem")]
    [AllowAnonymous]
    [AllowDuringSetup]
    [EnableRateLimiting("login-handoff")]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Redeem([FromBody] EmailConfirmationRedeemRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
            return InvalidLink();

        await using var db = await contexts.CreateAsync(ct);
        try
        {
            var redeemed = await loginCodes.RedeemAsync(
                db, request.Code.Trim(), [LoginCodePurpose.EmailConfirm],
                admits: async (code, token) =>
                    EmailAddresses.AreSame((await subjects.GetAsync(code.SubjectId, token))?.Email, code.Email),
                withSpend: async (code, token) =>
                {
                    if (await subjects.ConfirmEmailAsync(code.SubjectId, code.Email!, DateTime.UtcNow, token)
                        is not EmailConfirmation.Confirmed)
                        throw new AddressLeftException();
                },
                HttpContext.GetClientAddress(),
                Request.Headers.UserAgent.ToString(),
                ct);
            return redeemed is null ? InvalidLink() : NoContent();
        }
        catch (AddressLeftException)
        {
            return InvalidLink();
        }
    }

    private ObjectResult InvalidLink() => this.ProblemWithCode(
        AccountLinkProblemCodes.InvalidLink, "This link has expired or was already used.", StatusCodes.Status400BadRequest);

    private sealed class AddressLeftException : Exception;
}

/// <summary>The code from a confirmation link.</summary>
public class EmailConfirmationRedeemRequest
{
    public string? Code { get; set; }
}
