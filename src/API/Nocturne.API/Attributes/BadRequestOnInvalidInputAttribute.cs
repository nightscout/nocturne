using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Nocturne.API.Attributes;

/// <summary>
/// Answers an <see cref="ArgumentException"/> or <see cref="InvalidOperationException"/> from a
/// decorated action with the same 400 <c>ProblemDetails</c> that
/// <c>Problem(detail:, statusCode: 400, title: "Bad Request")</c> produces elsewhere, the message
/// in <c>detail</c>.
/// </summary>
/// <remarks>
/// Every other exception — <see cref="OperationCanceledException"/> included — is left unhandled,
/// so a server fault is reported as a 5xx by the global handler rather than as a client error.
/// </remarks>
/// <seealso cref="Middleware.ApiErrorEnvelopeHandler"/>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class BadRequestOnInvalidInputAttribute : Attribute, IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not (ArgumentException or InvalidOperationException))
            return;

        var problemDetails = context
            .HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>()
            .CreateProblemDetails(
                context.HttpContext,
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request",
                detail: context.Exception.Message
            );

        context.Result = new ObjectResult(problemDetails) { StatusCode = problemDetails.Status };
        context.ExceptionHandled = true;
    }
}
