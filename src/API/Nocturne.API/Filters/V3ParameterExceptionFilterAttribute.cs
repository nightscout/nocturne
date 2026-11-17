using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Controllers.V3;

namespace Nocturne.API.Filters;

/// <summary>
/// Answers a <see cref="V3ParameterException"/> with the 400 <c>{status, message}</c> body
/// Nightscout's V3 API sends for a rejected parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class V3ParameterExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not V3ParameterException exception)
            return;

        context.Result = new ObjectResult(new { status = 400, message = exception.Message }) { StatusCode = 400 };
        context.ExceptionHandled = true;
    }
}
