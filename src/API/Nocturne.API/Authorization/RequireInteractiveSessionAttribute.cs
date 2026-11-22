using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>
/// Refuses an authenticated request whose credential is not the person's own sign-in.
/// </summary>
/// <remarks>
/// Managing sign-in factors and issuing, widening, approving, listing or revoking credentials is the
/// account holder's decision. A delegated credential (an app's OAuth token, a direct grant, an
/// api-secret, a guest link, the instance key) holds only what it was given; one that could enrol a
/// factor or mint another credential would outlive its own revocation and expiry. The interactive
/// types are the ones that are the person, not a grant they gave out, by the rule
/// <see cref="PlatformAdminCredentials"/> states, less a provider's ID token sent as a bearer: the
/// provider vouches for its own sign-in, not for the second factor this instance asks of the
/// subject, and a bearer has no step-up to present one. This attribute keeps its own list so a
/// change to scope handling cannot move this boundary.
/// <para>
/// A session redeemed from a login code (<see cref="AuthContext.IsHandedOff"/>) is refused too:
/// someone else issued the code and redeeming it asks for no second factor. Its refusal carries
/// <see cref="SignInRequiredCode"/>, because unlike a delegated credential the person at that
/// browser can become the account holder by signing in, which replaces the session.
/// </para>
/// <para>
/// An unauthenticated request is left to the action: it answers with its own 401, or admits the
/// recovery session a spent recovery code mints, which no auth handler recognises.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireInteractiveSessionAttribute : Attribute, IAuthorizationFilter
{
    private static readonly IReadOnlySet<AuthType> SignInCredentialTypes = new HashSet<AuthType>
    {
        AuthType.SessionCookie,
        AuthType.LegacyJwt,
    };

    /// <summary>The <see cref="ProblemCodeExtensions.CodeExtension"/> of a handed-off session's refusal.</summary>
    public const string SignInRequiredCode = "sign_in_required";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!Refuses(context.HttpContext))
            return;

        context.Result = IsHandedOff(context.HttpContext)
            ? SignInRequired(context.HttpContext)
            : new ForbidResult();
    }

    /// <summary>
    /// Whether this filter refuses the request's caller, for an action that only sometimes mints a
    /// credential and so checks in its body instead.
    /// </summary>
    public static bool Refuses(HttpContext httpContext) =>
        httpContext.GetAuthContext() is { IsAuthenticated: true } auth
        && (!SignInCredentialTypes.Contains(auth.AuthType) || auth.IsHandedOff);

    /// <summary>Whether the caller is a session redeemed from a login code.</summary>
    public static bool IsHandedOff(HttpContext httpContext) =>
        httpContext.GetAuthContext() is { IsAuthenticated: true, IsHandedOff: true };

    /// <summary>
    /// The refusal a handed-off session reads, here and from an action that checks
    /// <see cref="Refuses"/> in its body.
    /// </summary>
    public static ObjectResult SignInRequired(HttpContext httpContext)
    {
        const string detail = "Sign in to do this. This session was opened from a sign-in link.";
        var problem = httpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>()
            .CreateProblemDetails(httpContext, StatusCodes.Status403Forbidden, title: "Forbidden", detail: detail);
        problem.Extensions[ProblemCodeExtensions.CodeExtension] = SignInRequiredCode;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status403Forbidden };
    }
}
