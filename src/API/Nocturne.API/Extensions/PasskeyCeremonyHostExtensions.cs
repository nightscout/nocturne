using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.Auth;

namespace Nocturne.API.Extensions;

/// <summary>
/// Refuses a WebAuthn ceremony the browser is certain to reject, in the terms an operator can act on.
/// </summary>
public static class PasskeyCeremonyHostExtensions
{
    /// <summary>
    /// The refusal to return instead of issuing WebAuthn options, or <c>null</c> when the host
    /// this request arrived on can complete a ceremony.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="HttpRequest.Host"/> rather than a forwarded header: the forwarded-headers
    /// middleware has already resolved the public host the browser used, and the same host has to
    /// decide both this and the tenant.
    /// </remarks>
    public static ActionResult? PasskeyHostRefusal(
        this ControllerBase controller, IPasskeyService passkeyService)
    {
        var detail = passkeyService.DescribeRpIdMismatch(controller.Request.Host.Host);

        return detail == null
            ? null
            : controller.Problem(detail: detail, statusCode: 400, title: "Bad Request");
    }
}
