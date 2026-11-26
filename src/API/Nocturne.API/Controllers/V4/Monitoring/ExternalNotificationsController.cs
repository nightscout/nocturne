using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Notifications.External;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Notices a connected app puts in members' notification trays. The app needs the
/// <c>notifications.send</c> scope, and each notice shows the app's registered name, marked as a
/// connected app.
/// </summary>
/// <remarks>
/// <para>
/// Only an OAuth access token issued to an app reaches these actions; a person's own sign-in and a
/// personal access token are refused with 403 <c>oauth_app_required</c>, even when they hold the
/// scope through <c>*</c>.
/// </para>
/// <para>
/// The tenant is the token's. By default a notice goes to the member who granted the app. Only when
/// that member is an owner or an administrator of the tenant may the app name the audience
/// <c>owners</c> or <c>members</c>. A link must be on the domain of one of the app's https redirect
/// URIs. An app may send 5 new keys to a tenant in a day; a repeat of a key it sent does not count.
/// </para>
/// <para>
/// The operator's counterpart is <see cref="PlatformAdmin.TenantNotificationsController"/>, and
/// both send through <see cref="IExternalNotificationService"/>.
/// </para>
/// </remarks>
[ApiController]
[ErrorEnvelope]
[Tags("Monitoring")]
[Route("api/v4/notifications/external")]
[Produces("application/json")]
[Authorize]
[RequireScope(Scope.NotificationsSend)]
public class ExternalNotificationsController(
    IExternalNotificationService notifications,
    IOAuthClientRepository clients) : ControllerBase
{
    /// <summary>The longest app name a notice shows.</summary>
    private const int MaxSenderNameLength = 100;

    /// <summary>
    /// Sends a notice to the member who granted the app, or, for an app an owner or an administrator
    /// granted, to the tenant's owners or members. Each member receives a key once: a repeat send
    /// reports the notice it made before, or <c>alreadySent</c> when that notice has since been
    /// deleted.
    /// </summary>
    /// <remarks>
    /// Refused with 429 <c>rate_limited</c> and a <c>Retry-After</c> header when the app sent 5 new
    /// keys to the tenant in the last day.
    /// </remarks>
    [HttpPost]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ExternalNotificationSendResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Send([FromBody] ExternalNotificationRequest request, CancellationToken ct)
    {
        var resolved = await ResolveAsync(ct);
        if (resolved.Refusal is { } refusal)
            return this.ExternalNotificationProblem(refusal);

        var outcome = await notifications.SendAsync(
            resolved.Sender!, HttpContext.GetTenantContext()!, request, HttpContext.GetAuthContext()!.SubjectId, ct);
        return outcome.Refusal is { } refused ? this.ExternalNotificationProblem(refused) : Ok(outcome.Result);
    }

    /// <summary>
    /// Withdraws the app's notice of <paramref name="key"/>: archives it for every member it reached.
    /// The key stays used, so sending it again sends nothing.
    /// </summary>
    [HttpDelete("{key}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ExternalNotificationWithdrawResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Withdraw(string key, CancellationToken ct)
    {
        var resolved = await ResolveAsync(ct);
        if (resolved.Refusal is { } refusal)
            return this.ExternalNotificationProblem(refusal);

        var outcome = await notifications.WithdrawAsync(resolved.Sender!, HttpContext.GetTenantContext()!, key, ct);
        return outcome.Refusal is { } refused ? this.ExternalNotificationProblem(refused) : Ok(outcome.Result);
    }

    private async Task<(ExternalNotificationSender? Sender, ExternalNotificationRefusal? Refusal)> ResolveAsync(
        CancellationToken ct)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth is not { AuthType: AuthType.OAuthAccessToken, SubjectId: not null, ClientId: { Length: > 0 } clientId })
        {
            return (null, new ExternalNotificationRefusal(
                StatusCodes.Status403Forbidden,
                "oauth_app_required",
                "Only a connected app may send notices here, with an OAuth access token."));
        }

        if (HttpContext.GetTenantContext() is not { } tenant)
            return (null, ExternalNotificationRefusal.TenantNotFound);
        if (!tenant.IsActive)
            return (null, ExternalNotificationRefusal.TenantInactive);

        var client = await clients.FindByClientIdAsync(clientId, ct);
        if (client is null)
        {
            return (null, new ExternalNotificationRefusal(
                StatusCodes.Status403Forbidden,
                "oauth_app_required",
                "The app this token was issued to is not registered on this site."));
        }

        var name = new[] { client.DisplayName, client.ClientName, client.ClientId }
            .First(n => !string.IsNullOrWhiteSpace(n))!.Trim();
        if (name.Length > MaxSenderNameLength)
            name = name[..MaxSenderNameLength];

        return (
            ExternalNotificationSender.App(
                client.ClientId, name, auth.TokenId, ExternalNotificationRules.AppLinkDomains(client.RedirectUris)),
            null);
    }
}
