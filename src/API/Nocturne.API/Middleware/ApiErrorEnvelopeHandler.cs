using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.API.Configuration;

namespace Nocturne.API.Middleware;

/// <summary>
/// Answers an unhandled exception from an <see cref="ErrorEnvelopeAttribute"/> action with the
/// error envelope its API version already uses.
/// </summary>
/// <remarks>
/// The envelope is a wire contract, not a presentation choice — Nightscout clients parse it — so
/// each version keeps the shape it shipped with: V1 <c>{status, message, type, error}</c>, V3
/// <c>{status, message}</c>, and V2/V4 an RFC 9457 <c>ProblemDetails</c>. The V1 <c>error</c>
/// member carries <see cref="Exception.Message"/>, which uploaders surface to the user.
///
/// V1-V3 bodies are written with <see cref="NightscoutJsonOptions"/> because
/// <see cref="NightscoutJsonFilter"/> serialises every other result on those routes that way, and
/// a result filter cannot run once the pipeline has unwound to the exception handler.
/// </remarks>
/// <seealso cref="ErrorEnvelopeAttribute"/>
internal sealed class ApiErrorEnvelopeHandler : IExceptionHandler
{
    private const string Detail = "Internal server error";
    private const string Title = "Internal Server Error";
    private const string ContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions NightscoutOptions = NightscoutJsonOptions.Create();

    private readonly ILogger<ApiErrorEnvelopeHandler> _logger;
    private readonly ProblemDetailsFactory _problemDetailsFactory;
    private readonly JsonSerializerOptions _mvcOptions;

    public ApiErrorEnvelopeHandler(
        ILogger<ApiErrorEnvelopeHandler> logger,
        ProblemDetailsFactory problemDetailsFactory,
        IOptions<JsonOptions> mvcJsonOptions
    )
    {
        _logger = logger;
        _problemDetailsFactory = problemDetailsFactory;
        _mvcOptions = mvcJsonOptions.Value.JsonSerializerOptions;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        // ExceptionHandlerMiddleware clears the endpoint before running handlers; the feature
        // carries it forward.
        var endpoint =
            httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint
            ?? httpContext.GetEndpoint();

        if (endpoint?.Metadata.GetMetadata<ErrorEnvelopeAttribute>() is null)
        {
            return false;
        }

        _logger.LogError(
            exception,
            "Unhandled exception serving {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path
        );

        var (body, options) = NightscoutApiPath.Version(httpContext.Request.Path) switch
        {
            1 => (
                (object)new
                {
                    status = StatusCodes.Status500InternalServerError,
                    message = Detail,
                    type = "internal",
                    error = exception.Message,
                },
                NightscoutOptions
            ),
            3 => (
                new { status = StatusCodes.Status500InternalServerError, message = Detail },
                NightscoutOptions
            ),
            2 => (CreateProblem(httpContext), NightscoutOptions),
            _ => (CreateProblem(httpContext), _mvcOptions),
        };

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(
            body,
            options,
            contentType: ContentType,
            cancellationToken
        );

        return true;
    }

    private ProblemDetails CreateProblem(HttpContext httpContext) =>
        _problemDetailsFactory.CreateProblemDetails(
            httpContext,
            statusCode: StatusCodes.Status500InternalServerError,
            title: Title,
            detail: Detail
        );
}
