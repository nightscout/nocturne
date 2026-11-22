using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Extensions;

/// <summary>Writes the cookies a <see cref="SignInOutcome"/> calls for, the same ones for every method.</summary>
public static class SignInOutcomeExtensions
{
    /// <summary>
    /// A session: its cookies, the end of a handed-off session it replaces, and the sign-in hint. A
    /// code still owed: the hint only, since the primary factor is what the login form leads with.
    /// A refusal: nothing.
    /// </summary>
    /// <remarks>
    /// The code step writes no hint: the form offers no code-only sign-in, and the primary factor
    /// wrote the hint when the code was asked for.
    /// </remarks>
    public static async Task ApplySignInAsync(this HttpContext context, SignInOutcome outcome, OidcOptions options)
    {
        switch (outcome)
        {
            case SignInOutcome.SignedIn signedIn:
                context.Response.SetSessionCookies(signedIn.Session, options);
                await context.EndReplacedHandedOffSessionAsync(options);
                SetHint(context.Response, signedIn.Method, signedIn.ProviderId, options);
                break;
            case SignInOutcome.SecondFactorRequired required:
                SetHint(context.Response, required.Method, required.ProviderId, options);
                break;
        }
    }

    private static void SetHint(HttpResponse response, SignInMethod method, Guid? providerId, OidcOptions options)
    {
        switch (method)
        {
            case SignInMethod.Passkey:
                response.SetLastSignInCookie(SessionCookieExtensions.SignInMethods.Passkey, providerId: null, options);
                break;
            case SignInMethod.Oidc:
                response.SetLastSignInCookie(
                    SessionCookieExtensions.SignInMethods.Oidc, providerId?.ToString(), options);
                break;
        }
    }
}
