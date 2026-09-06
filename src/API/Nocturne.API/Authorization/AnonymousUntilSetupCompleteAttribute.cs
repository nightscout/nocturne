using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Identity;

namespace Nocturne.API.Authorization;

/// <summary>
/// Narrows an <c>[AllowAnonymous]</c> endpoint to first-run setup: anonymous while the instance
/// has no account anyone can sign in with, authenticated callers only once it has.
/// </summary>
/// <remarks>
/// For endpoints the setup wizard needs before any credential exists, whether or not anything
/// still calls them once it does. Left permanently anonymous, such an endpoint answers questions
/// about the instance to anyone who asks — a name-availability probe is a membership oracle for
/// the set of names in use, and rate limiting bounds the probe rate without bounding the sweep.
/// <para>
/// A plain MVC authorization filter rather than a policy: <c>[AllowAnonymous]</c> suppresses the
/// authorization middleware's policy evaluation, so a policy attached here would never be asked.
/// Filters are not suppressed, so this runs on every request to the endpoint.
/// </para>
/// <para>
/// The refusal reads only the request's own credentials and the instance's setup state, never the
/// caller's input, so it costs the same and says the same thing whatever was asked about.
/// </para>
/// </remarks>
/// <seealso cref="IInstanceSetupState"/>
/// <seealso cref="DenyDemoSubjectAttribute"/>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AnonymousUntilSetupCompleteAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.IsAuthenticated())
            return;

        var setupState = context.HttpContext.RequestServices.GetRequiredService<IInstanceSetupState>();

        if (await setupState.IsSetupCompleteAsync(context.HttpContext.RequestAborted))
            context.Result = new UnauthorizedResult();
    }
}
