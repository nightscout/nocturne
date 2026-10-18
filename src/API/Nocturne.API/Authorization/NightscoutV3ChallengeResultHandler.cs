using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Middleware;

namespace Nocturne.API.Authorization;

/// <summary>
/// Adds the V3 <c>{status, message}</c> body to a challenge on a <c>/api/v3/…</c> path, keeping the
/// status and <c>WWW-Authenticate</c> header the challenge already wrote.
/// </summary>
/// <remarks>
/// Nightscout answers a missing or rejected credential on every V3 operation with this body.
/// <see cref="Middleware.AuthenticationMiddleware"/> reduces a rejected credential to anonymous,
/// so the two cases share one message.
/// </remarks>
internal sealed class NightscoutV3ChallengeResultHandler(
    ProblemDetailsFactory problemDetailsFactory,
    IOptions<JsonOptions> mvcJsonOptions) : IAuthorizationMiddlewareResultHandler
{
    public const string Message = "Missing or bad access token or JWT";

    private readonly AuthorizationMiddlewareResultHandler _inner = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        await _inner.HandleAsync(next, context, policy, authorizeResult);

        if (!authorizeResult.Challenged
            || context.Response.HasStarted
            || NightscoutApiPath.Version(context.Request.Path) != 3)
        {
            return;
        }

        var (body, options) = ApiErrorEnvelope.Create(
            context,
            problemDetailsFactory,
            mvcJsonOptions.Value.JsonSerializerOptions,
            StatusCodes.Status401Unauthorized,
            "Unauthorized",
            Message,
            type: "unauthorized",
            error: Message);

        context.Response.ContentType = ApiErrorEnvelope.ContentType;
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), options, context.RequestAborted);
    }
}
