using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
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
/// types are exactly <see cref="MemberScopeResolver.UnscopedCredentialTypes"/>: the ones that carry
/// no grant of their own and resolve to the member's role.
/// <para>
/// An unauthenticated request is left to the action: it answers with its own 401, or admits the
/// recovery session a spent recovery code mints, which no auth handler recognises.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireInteractiveSessionAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (Refuses(context.HttpContext))
        {
            context.Result = new ForbidResult();
        }
    }

    /// <summary>
    /// Whether this filter refuses the request's caller, for an action that only sometimes mints a
    /// credential and so checks in its body instead.
    /// </summary>
    public static bool Refuses(HttpContext httpContext) =>
        httpContext.GetAuthContext() is { IsAuthenticated: true } auth
        && !MemberScopeResolver.UnscopedCredentialTypes.Contains(auth.AuthType);
}
