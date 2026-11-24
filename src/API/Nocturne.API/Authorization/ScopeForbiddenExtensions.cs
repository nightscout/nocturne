using Microsoft.AspNetCore.Mvc;

namespace Nocturne.API.Authorization;

/// <summary>
/// The refusal a per-record scope guard answers from the handler, where the required scope depends
/// on the record or a request value, so no attribute can state it.
/// </summary>
/// <seealso cref="Attributes.ScopeDecidedByAttribute"/>
internal static class ScopeForbiddenExtensions
{
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="scope">The scope the caller is missing.</param>
    public static ScopeForbiddenResult ForbiddenForScope(this ControllerBase controller, string scope) =>
        new ScopeForbiddenResult([scope]).WithBody(controller.ProblemDetailsFactory, controller.HttpContext);
}
