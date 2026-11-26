using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Notifications.External;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>
/// Notices from the operator to a tenant's owners or members. The operator sends with the instance
/// key; each notice shows the operator's name.
/// </summary>
/// <remarks>
/// A notice is information only: it stays in the notification tray, with no banner, push or sound.
/// The operator is not rate limited. The connected-app counterpart is
/// <see cref="Monitoring.ExternalNotificationsController"/>, and both send through
/// <see cref="IExternalNotificationService"/>.
/// </remarks>
[ApiController]
[ErrorEnvelope]
[System.ComponentModel.DisplayName("Tenant notifications")]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/tenants/{tenantId:guid}/notifications")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
[AllowDuringSetup]
public class TenantNotificationsController(
    IExternalNotificationService notifications,
    ITenantService tenants,
    IOptions<OperatorConfiguration> operatorOptions) : ControllerBase
{
    /// <summary>
    /// Sends a notice to the tenant's owners or to all its members. Each member receives a key once:
    /// a repeat send reports the notice it made before, or <c>alreadySent</c> when that notice has
    /// since been deleted. A tenant with no owner gets an owners notice nowhere.
    /// </summary>
    /// <remarks>
    /// Needs the instance key. Refused with 409 <c>operator_not_configured</c> while
    /// <c>Operator:Name</c> is unset, since every notice shows it. A link must be on a domain of
    /// <c>Operator:NotificationLinkDomains</c>, or of the operator's support URLs while that is empty.
    /// </remarks>
    [HttpPost]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ExternalNotificationSendResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Send(
        Guid tenantId, [FromBody] ExternalNotificationRequest request, CancellationToken ct)
    {
        if (!IsOperator())
            return Forbid();

        var resolved = await ResolveAsync(tenantId, ct);
        if (resolved.Refusal is { } refusal)
            return this.ExternalNotificationProblem(refusal);

        var outcome = await notifications.SendAsync(resolved.Sender!, resolved.Tenant!, request, null, ct);
        return outcome.Refusal is { } refused ? this.ExternalNotificationProblem(refused) : Ok(outcome.Result);
    }

    /// <summary>
    /// Withdraws the notice of <paramref name="key"/>: archives it for every member it reached. The
    /// key stays used, so sending it again sends nothing.
    /// </summary>
    [HttpDelete("{key}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ExternalNotificationWithdrawResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Withdraw(Guid tenantId, string key, CancellationToken ct)
    {
        if (!IsOperator())
            return Forbid();

        var resolved = await ResolveAsync(tenantId, ct);
        if (resolved.Refusal is { } refusal)
            return this.ExternalNotificationProblem(refusal);

        var outcome = await notifications.WithdrawAsync(resolved.Sender!, resolved.Tenant!, key, ct);
        return outcome.Refusal is { } refused ? this.ExternalNotificationProblem(refused) : Ok(outcome.Result);
    }

    /// <summary>The instance key: platform administration with no person behind it.</summary>
    private bool IsOperator() =>
        HttpContext.GetAuthContext() is { IsPlatformAdmin: true, AuthType: AuthType.InstanceKey, SubjectId: null };

    private async Task<(ExternalNotificationSender? Sender, TenantContext? Tenant, ExternalNotificationRefusal? Refusal)> ResolveAsync(
        Guid tenantId, CancellationToken ct)
    {
        var config = operatorOptions.Value;
        if (string.IsNullOrWhiteSpace(config.Name))
        {
            return (null, null, new ExternalNotificationRefusal(
                StatusCodes.Status409Conflict,
                "operator_not_configured",
                "Operator:Name is not set. Operator notices show it, so none can be sent."));
        }

        var tenant = await tenants.GetByIdAsync(tenantId, ct);
        if (tenant is null)
            return (null, null, ExternalNotificationRefusal.TenantNotFound);
        if (!tenant.IsActive)
            return (null, null, ExternalNotificationRefusal.TenantInactive);

        return (
            ExternalNotificationSender.Operator(config.Name.Trim(), config.ResolveNotificationLinkDomains()),
            new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, IsDemo: false),
            null);
    }
}
