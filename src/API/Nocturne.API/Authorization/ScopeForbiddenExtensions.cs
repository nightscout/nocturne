using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Nocturne.API.Authorization;

/// <summary>
/// Shared 403 response for the controllers that enforce a write scope in the handler rather than
/// through <see cref="Attributes.RequireScopeAttribute"/> — the ones whose required scope depends on
/// the record being written, so no attribute scan can determine it.
/// </summary>
/// <seealso cref="ActivityWriteScopeGuard"/>
/// <seealso cref="StateSpanWriteScopeGuard"/>
internal static class ScopeForbiddenExtensions
{
    /// <summary>
    /// Returns the 403 a per-record scope guard produces, worded the same way for every caller so
    /// the response does not reveal which controller enforced it.
    /// </summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="scope">The scope the caller is missing.</param>
    public static ObjectResult ForbiddenForScope(this ControllerBase controller, string scope) =>
        controller.Problem(
            detail: $"This operation requires the '{scope}' scope.",
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden");
}
