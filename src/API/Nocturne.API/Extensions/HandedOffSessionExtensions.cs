using System.Text.Json;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Extensions;

/// <summary>Ends a session redeemed from a login code once the person signs in over it.</summary>
public static class HandedOffSessionExtensions
{
    /// <summary>
    /// Revokes the handed-off session whose refresh token the request presents, after a sign-in
    /// set its own session cookies over it.
    /// </summary>
    /// <remarks>
    /// A sign-in replaces the browser's cookies and revokes nothing, so without this the handed-off
    /// chain would stay live for anyone holding its refresh token, beside the session that is now
    /// the person's own sign-in. The end is recorded as a sign-out of that session.
    /// </remarks>
    public static async Task EndReplacedHandedOffSessionAsync(this HttpContext context, OidcOptions options)
    {
        if (context.Request.Cookies[options.Cookie.RefreshTokenName] is not { Length: > 0 } presented)
            return;

        var refreshTokens = context.RequestServices.GetRequiredService<IRefreshTokenService>();
        if (await refreshTokens.FindOwnerAsync(presented) is not { HandedOff: true } owner)
            return;

        await refreshTokens.RevokeSessionForSubjectAsync(owner.SubjectId, owner.SessionId, "Replaced by a sign-in");
        await context.RequestServices.GetRequiredService<IAuthAuditService>().LogAsync(
            AuthAuditEventType.Logout, owner.SubjectId, success: true,
            ipAddress: context.GetClientAddress(),
            userAgent: context.Request.Headers.UserAgent.ToString(),
            detailsJson: JsonSerializer.Serialize(new { reason = "replaced_by_sign_in" }));
    }
}
