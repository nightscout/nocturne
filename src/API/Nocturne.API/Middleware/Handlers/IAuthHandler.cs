using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Interface for authentication handlers in the middleware pipeline.
/// Handlers are executed in order of priority (lowest first).
/// </summary>
public interface IAuthHandler
{
    /// <summary>
    /// Handler priority. Lower values execute first.
    /// Recommended priorities:
    /// - Session Cookie: 50
    /// - Instance Key: 55
    /// - Direct Grant Token: 150
    /// - Session bearer JWT: 200
    /// - Access Token: 300
    /// - API Secret: 400
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Display name for logging
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Attempt to authenticate the request.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>
    /// AuthResult.Success if credentials are valid,
    /// AuthResult.Failure if credentials are invalid,
    /// AuthResult.Skip if this handler doesn't recognize the credentials
    /// </returns>
    Task<AuthResult> AuthenticateAsync(HttpContext context);
}
