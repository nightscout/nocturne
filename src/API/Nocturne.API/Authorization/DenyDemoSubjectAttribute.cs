using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Auth;

namespace Nocturne.API.Authorization;

/// <summary>
/// Refuses a request authenticated as a demo tenant's shared visitor account.
/// </summary>
/// <remarks>
/// A demo session is handed to any anonymous caller, so the subject behind it is
/// authenticated but stands for no one. Endpoints that read "authenticated" as "a person
/// who signed up" — creating a tenant, accepting an invite, requesting membership — would
/// otherwise let an anonymous visitor act as a platform user, and act as the <em>same</em>
/// platform user as every other visitor.
/// <para>
/// Applied to the tenantless, subject-scoped endpoints, where a tenant's own permission
/// checks cannot help because there is no tenant in the request to check against.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DenyDemoSubjectAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (await RefusesAsync(context.HttpContext))
        {
            context.Result = new ForbidResult();
        }
    }

    /// <summary>
    /// Whether this filter refuses the request's caller. <c>GET /api/v4/me/permissions</c> reports
    /// it so the web can withhold the controls these endpoints would refuse, since the demo
    /// member holds the scopes that would otherwise offer them.
    /// </summary>
    public static async Task<bool> RefusesAsync(HttpContext httpContext)
    {
        // EffectiveSubjectId rather than SubjectId: a guest session carries the data owner in
        // ActingAsSubjectId and leaves SubjectId null, so keying on SubjectId alone would let an
        // authenticated credential whose subject is the demo account past the gate unexamined.
        if (httpContext.GetAuthContext() is not { EffectiveSubjectId: { } subjectId })
            return false;

        var subjects = httpContext.RequestServices.GetRequiredService<ISubjectRepository>();

        // A missing row is refused, not waved through: an access token is a self-contained JWT
        // with no revocation check, so a subject deleted mid-token, which every demo reset does,
        // would otherwise read as "not a demo subject".
        var isDemoSubject = await subjects.IsDemoSubjectAsync(subjectId, httpContext.RequestAborted);

        return isDemoSubject is null or true;
    }
}
