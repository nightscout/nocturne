using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Nocturne.API.Attributes;
using Nocturne.API.Configuration;
using Nocturne.API.Extensions;

namespace Nocturne.API.Authorization;

/// <summary>
/// The 403 a caller reads when it lacks the scope an action requires: a <see cref="ProblemDetails"/>
/// whose detail names the scopes, and whose <see cref="ProblemCodeExtensions.CodeExtension"/> and
/// <see cref="ScopesExtension"/> let a client say what is missing in its own words. The scope attributes and
/// the per-record guards answer with it alike, so a refusal does not reveal which of them enforced it.
/// </summary>
/// <remarks>
/// The body is built when the result executes unless it was built already. An action's result is
/// built at once (<see cref="ScopeForbiddenExtensions.ForbiddenForScope"/>), because result filters
/// see it first and <see cref="NightscoutJsonFilter"/> re-serialises the value it holds then.
/// </remarks>
/// <seealso cref="Attributes.RequireScopeAttribute"/>
/// <seealso cref="Attributes.RequireDeclaredWriteScopeAttribute"/>
public sealed class ScopeForbiddenResult : ObjectResult
{
    /// <summary>The <see cref="ProblemCodeExtensions.CodeExtension"/> every scope refusal carries.</summary>
    public const string MissingScopeCode = "missing_scope";

    /// <summary>The <see cref="ProblemDetails.Extensions"/> key holding <see cref="Scopes"/>.</summary>
    public const string ScopesExtension = "scopes";

    /// <param name="scopes">The scopes the action accepts.</param>
    /// <param name="requiresAll">Whether the action needs every one of <paramref name="scopes"/> rather than any.</param>
    public ScopeForbiddenResult(IReadOnlyList<string> scopes, bool requiresAll = false)
        : base(null)
    {
        Scopes = scopes;
        RequiresAll = requiresAll;
        StatusCode = StatusCodes.Status403Forbidden;
    }

    public IReadOnlyList<string> Scopes { get; }

    public bool RequiresAll { get; }

    public string Detail => Scopes.Count switch
    {
        1 => $"This operation requires the '{Scopes[0]}' scope.",
        _ when RequiresAll => $"This operation requires all of these scopes: {Quoted()}.",
        _ => $"This operation requires one of these scopes: {Quoted()}.",
    };

    /// <summary>
    /// The refusal a scope attribute answers for the request's action. A Nightscout action keeps the
    /// bare 403 it answered before the scope was named, which the clients that call it already
    /// handle: one on a V1, V2 or V3 path, or one marked <see cref="NightscoutEndpointAttribute"/>
    /// outside those paths (<c>/pebble</c>).
    /// </summary>
    public static IActionResult For(HttpContext httpContext, IReadOnlyList<string> scopes, bool requiresAll = false) =>
        IsNightscoutAction(httpContext)
            ? new ForbidResult()
            : new ScopeForbiddenResult(scopes, requiresAll);

    private static bool IsNightscoutAction(HttpContext httpContext) =>
        NightscoutApiPath.Version(httpContext.Request.Path) is not null
        || httpContext.GetEndpoint()?.Metadata.GetMetadata<NightscoutEndpointAttribute>() is not null;

    /// <remarks>
    /// On a Nightscout action the body has no code or scopes, so a V1 guard's refusal stays the body
    /// its clients already parse.
    /// Without a factory the body is built as <see cref="ControllerBase.Problem(string, string, int?, string, string)"/>
    /// builds it then.
    /// </remarks>
    internal ScopeForbiddenResult WithBody(ProblemDetailsFactory? factory, HttpContext httpContext)
    {
        var problem = factory?.CreateProblemDetails(
                httpContext,
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: Detail)
            ?? new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = Detail,
            };
        if (!IsNightscoutAction(httpContext))
        {
            problem.Extensions[ProblemCodeExtensions.CodeExtension] = MissingScopeCode;
            problem.Extensions[ScopesExtension] = Scopes.ToArray();
        }

        Value = problem;
        return this;
    }

    public override Task ExecuteResultAsync(ActionContext context)
    {
        if (Value is null)
        {
            WithBody(
                context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>(),
                context.HttpContext);
        }

        return base.ExecuteResultAsync(context);
    }

    private string Quoted() => string.Join(", ", Scopes.Select(scope => $"'{scope}'"));
}
