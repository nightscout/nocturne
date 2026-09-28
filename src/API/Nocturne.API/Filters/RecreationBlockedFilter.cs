using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.API.Middleware;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Filters;

/// <summary>
/// Answers a <see cref="RecreationBlockedException"/> with <c>409 Conflict</c>: the
/// <see cref="ProblemDetails"/> body that <see cref="ControllerBase.Problem(string, string, int?, string, string)"/> would have produced,
/// or, for an <see cref="ErrorEnvelopeAttribute"/> action, the error envelope its API version uses.
/// </summary>
/// <remarks>
/// Registered globally rather than caught in
/// <see cref="Controllers.V4.Base.V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>:
/// several controllers override <c>Create</c> without calling the base, and the repositories that
/// raise it are reachable from actions outside the CRUD base entirely. Handling the exception here
/// keeps it from <see cref="ApiErrorEnvelopeHandler"/>, so the envelope is answered here too.
/// </remarks>
public sealed class RecreationBlockedFilter(
    ProblemDetailsFactory problemDetailsFactory,
    IOptions<JsonOptions> mvcJsonOptions) : IExceptionFilter
{
    private const string Title = "Conflict";

    private readonly JsonSerializerOptions _mvcOptions = mvcJsonOptions.Value.JsonSerializerOptions;

    /// <inheritdoc />
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not RecreationBlockedException blocked)
            return;

        context.Result = context.ActionDescriptor.EndpointMetadata.OfType<ErrorEnvelopeAttribute>().Any()
            ? Envelope(context.HttpContext, blocked)
            : Problem(context.HttpContext, blocked);
        context.ExceptionHandled = true;
    }

    private ObjectResult Problem(HttpContext httpContext, RecreationBlockedException blocked)
    {
        var problem = problemDetailsFactory.CreateProblemDetails(
            httpContext,
            statusCode: StatusCodes.Status409Conflict,
            title: Title,
            detail: blocked.Message);

        return new ObjectResult(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" },
        };
    }

    private JsonResult Envelope(HttpContext httpContext, RecreationBlockedException blocked)
    {
        var (body, options) = ApiErrorEnvelope.Create(
            httpContext,
            problemDetailsFactory,
            _mvcOptions,
            StatusCodes.Status409Conflict,
            Title,
            blocked.Message,
            type: "conflict",
            error: blocked.Message);

        return new JsonResult(body, options)
        {
            StatusCode = StatusCodes.Status409Conflict,
            ContentType = ApiErrorEnvelope.ContentType,
        };
    }
}
