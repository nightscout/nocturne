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
                // Decided first so a method with no hint decision throws before any cookie is
                // written or the handed-off session is ended.
                var hint = HintFor(signedIn.Method, signedIn.ProviderId);
                context.Response.SetSessionCookies(signedIn.Session, options);
                await context.EndReplacedHandedOffSessionAsync(options);
                SetHint(context.Response, hint, options);
                break;
            case SignInOutcome.SecondFactorRequired required:
                SetHint(context.Response, HintFor(required.Method, required.ProviderId), options);
                break;
            case SignInOutcome.Refused:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    /// <summary>The sign-in hint a method writes, or null for one that writes none.</summary>
    private static SignInHint? HintFor(SignInMethod method, Guid? providerId) => method switch
    {
        SignInMethod.Passkey => new SignInHint(SessionCookieExtensions.SignInMethods.Passkey, null),
        SignInMethod.Oidc => new SignInHint(SessionCookieExtensions.SignInMethods.Oidc, providerId?.ToString()),
        SignInMethod.Totp => null,
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    private static void SetHint(HttpResponse response, SignInHint? hint, OidcOptions options)
    {
        if (hint is { } value)
            response.SetLastSignInCookie(value.Method, value.ProviderId, options);
    }

    private readonly record struct SignInHint(string Method, string? ProviderId);
}
