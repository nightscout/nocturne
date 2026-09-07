using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Filters;

/// <summary>
/// Answers a <see cref="RecreationBlockedException"/> with the <c>409 Conflict</c>
/// <see cref="ProblemDetails"/> body that <see cref="ControllerBase.Problem"/> would have produced.
/// </summary>
/// <remarks>
/// Registered globally rather than caught in
/// <see cref="Controllers.V4.Base.V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>:
/// several controllers override <c>Create</c> without calling the base, and the repositories that
/// raise it are reachable from actions outside the CRUD base entirely.
/// </remarks>
public sealed class RecreationBlockedFilter(ProblemDetailsFactory problemDetailsFactory) : IExceptionFilter
{
    /// <inheritdoc />
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not RecreationBlockedException blocked)
            return;

        var problem = problemDetailsFactory.CreateProblemDetails(
            context.HttpContext,
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: blocked.Message);

        context.Result = new ObjectResult(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" },
        };
        context.ExceptionHandled = true;
    }
}
