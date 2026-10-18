using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// A <see cref="ProblemDetailsFactory"/> for a controller built outside MVC, which has none
/// registered. It carries exactly what the caller hands it; the MVC-registered factory is
/// exercised end to end by <see cref="GoldenFiles.RecreationBlockedPipelineTests"/>.
/// </summary>
internal sealed class EchoingProblemDetailsFactory : ProblemDetailsFactory
{
    public override ProblemDetails CreateProblemDetails(
        HttpContext httpContext, int? statusCode = null, string? title = null,
        string? type = null, string? detail = null, string? instance = null) =>
        new() { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };

    public override ValidationProblemDetails CreateValidationProblemDetails(
        HttpContext httpContext, ModelStateDictionary modelStateDictionary, int? statusCode = null,
        string? title = null, string? type = null, string? detail = null, string? instance = null) =>
        new(modelStateDictionary) { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };
}
