using Microsoft.AspNetCore.Mvc;

namespace Nocturne.API.Extensions;

/// <summary>The one way a controller answers with a problem the web app translates by code.</summary>
public static class ProblemCodeExtensions
{
    /// <summary>The <see cref="ProblemDetails.Extensions"/> key the web app reads.</summary>
    public const string CodeExtension = "code";

    /// <summary>A problem response that carries <paramref name="code"/> under <see cref="CodeExtension"/>.</summary>
    /// <param name="controller">The controller answering, whose problem details factory builds the body.</param>
    /// <param name="code">A stable lower snake_case constant, defined once for its feature.</param>
    /// <param name="detail">The fallback text for a client that does not know <paramref name="code"/>.</param>
    /// <param name="statusCode">The HTTP status of the response.</param>
    /// <param name="title">The problem title, or null for the status phrase.</param>
    public static ObjectResult ProblemWithCode(
        this ControllerBase controller, string code, string? detail, int statusCode, string? title = null)
    {
        var result = controller.Problem(detail: detail, statusCode: statusCode, title: title);
        ((ProblemDetails)result.Value!).Extensions[CodeExtension] = code;
        return result;
    }
}
