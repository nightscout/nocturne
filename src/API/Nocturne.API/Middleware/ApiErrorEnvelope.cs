using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Nocturne.API.Configuration;

namespace Nocturne.API.Middleware;

/// <summary>
/// The per-version error body an <see cref="Attributes.ErrorEnvelopeAttribute"/> action answers
/// with, and the serializer options it must be written with. Why each version keeps its shape is
/// on <see cref="ApiErrorEnvelopeHandler"/>.
/// </summary>
internal static class ApiErrorEnvelope
{
    /// <summary>The content type every envelope is written with.</summary>
    public const string ContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions NightscoutOptions = NightscoutJsonOptions.Create();

    /// <param name="httpContext">The request being answered; its path picks the version.</param>
    /// <param name="problemDetailsFactory">Builds the V2/V4 body.</param>
    /// <param name="mvcOptions">The V4 serializer options.</param>
    /// <param name="status">The HTTP status the body reports.</param>
    /// <param name="title">The ProblemDetails title.</param>
    /// <param name="message">The V1/V3 <c>message</c> and the ProblemDetails detail.</param>
    /// <param name="type">The V1 <c>type</c>.</param>
    /// <param name="error">The V1 <c>error</c>, which uploaders surface to the user.</param>
    public static (object Body, JsonSerializerOptions Options) Create(
        HttpContext httpContext,
        ProblemDetailsFactory problemDetailsFactory,
        JsonSerializerOptions mvcOptions,
        int status,
        string title,
        string message,
        string type,
        string error)
    {
        return NightscoutApiPath.Version(httpContext.Request.Path) switch
        {
            1 => ((object)new { status, message, type, error }, NightscoutOptions),
            3 => (new { status, message }, NightscoutOptions),
            2 => (Problem(), NightscoutOptions),
            _ => (Problem(), mvcOptions),
        };

        object Problem() => problemDetailsFactory.CreateProblemDetails(
            httpContext, statusCode: status, title: title, detail: message);
    }
}
