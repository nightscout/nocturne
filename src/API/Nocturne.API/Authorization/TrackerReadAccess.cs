using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>
/// Who reads the tenant's trackers, wherever they are served: the tracker endpoints, the dashboard
/// chart's tracker markers and the widget summary's tracker statuses.
/// </summary>
/// <remarks>
/// Trackers are the tenant's, so a reader sees all of them or none, and a definition's
/// <c>Visibility</c> narrows nothing. A reader is an account holding <see cref="Scope.AlertsRead"/>
/// (which <see cref="Scope.AlertsReadWrite"/> and full access satisfy), the read half of the
/// <see cref="Scope.AlertsReadWrite"/> that tracker writes require. A viewer without an account
/// reads none even if it holds the scope: a share is already blind to the tracker tables under RLS
/// (they are not in <see cref="ShareDataCategories"/>), and a guest link, which RLS does not hide
/// them from, is refused here the same way.
/// </remarks>
public static class TrackerReadAccess
{
    /// <summary>Whether the request's caller reads the tenant's trackers.</summary>
    public static bool Allows(HttpContext httpContext) =>
        httpContext.IsAuthenticated()
        && !DenyNoAccountViewerAttribute.Refuses(httpContext)
        && httpContext.HasScope(Scope.AlertsRead);
}
