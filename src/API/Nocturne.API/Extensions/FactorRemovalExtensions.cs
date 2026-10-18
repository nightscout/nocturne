using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.Auth;

namespace Nocturne.API.Extensions;

internal static class FactorRemovalExtensions
{
    /// <summary>
    /// The one refusal every sign-in factor removal endpoint answers with, so a client reads the same
    /// status and body whichever route it called.
    /// </summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">A result other than <see cref="FactorRemovalResult.Removed"/>.</param>
    /// <param name="factor">The factor's name as it reads in a sentence, such as "Passkey".</param>
    public static ObjectResult FactorRemovalRefusal(
        this ControllerBase controller, FactorRemovalResult result, string factor) =>
        result switch
        {
            FactorRemovalResult.NotFound => controller.Problem(
                detail: $"{factor} not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Not Found"),
            // An external client can branch on this code; the web's remote functions see only the status.
            FactorRemovalResult.LastPrimaryFactor => controller.Problem(
                detail: "Cannot remove the only remaining sign-in method.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                extensions: new Dictionary<string, object?> { ["error"] = "last_factor" }),
            _ => throw new InvalidOperationException($"Unexpected FactorRemovalResult: {result}"),
        };
}
