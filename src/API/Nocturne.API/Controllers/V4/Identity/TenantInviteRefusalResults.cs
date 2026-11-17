using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>The HTTP answer for a <see cref="TenantInviteRefusal"/>.</summary>
internal static class TenantInviteRefusalResults
{
    /// <summary>
    /// 404 for an unknown link and 409 for a request the instance's state refuses. Every other
    /// refusal is a 400 ProblemDetails, as for member invites: the generated client passes a 400's
    /// detail through to the page, which shows it, where other statuses collapse to a generic
    /// message.
    /// </summary>
    public static IActionResult ToResult(this ControllerBase controller, TenantInviteRefusal refusal) =>
        refusal.Code switch
        {
            TenantInviteRefusalCode.NotFound => controller.Problem(
                detail: refusal.Message, statusCode: StatusCodes.Status404NotFound, title: "Not Found"),
            TenantInviteRefusalCode.BaseDomainRequired or TenantInviteRefusalCode.SubdomainChangeUnconfirmed =>
                controller.Problem(detail: refusal.Message, statusCode: StatusCodes.Status409Conflict, title: "Conflict"),
            _ => controller.Problem(
                detail: refusal.Message, statusCode: StatusCodes.Status400BadRequest, title: "Bad Request"),
        };
}
