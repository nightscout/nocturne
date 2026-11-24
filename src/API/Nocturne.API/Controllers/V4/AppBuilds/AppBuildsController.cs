using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.AppBuilds;
using Nocturne.Core.Models.AppBuilds;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.AppBuilds;

/// <summary>
/// Sets up, starts and watches the tenant owner's own app build in their private GitHub repository,
/// through the instance's GitHub App (<see cref="AppBuildService"/>).
/// </summary>
/// <remarks>
/// Writing a signing-key secret to a person's repository and starting a build there is the account
/// holder's decision. So every action, the installation callback included, needs
/// <see cref="Scope.TenantSettings"/> from the person's own signed-in session (no delegated credential,
/// share, demo visitor or history-clamped caller).
/// <para>
/// Choosing the repository, writing a secret and starting a build are further limited to the person who
/// linked the installation (<see cref="AppBuildService"/>).
/// </para>
/// </remarks>
[ApiController, Authorize]
[RequireScope(Scope.TenantSettings), DenyDemoSubject, RequireInteractiveSession, RefuseHistoryClamped]
[Route("api/v4/app-builds")]
[Tags("TenantAdmin")]
[ErrorEnvelope]
[ProducesResponseType(typeof(ProblemDetails), 400)]
[ProducesResponseType(typeof(ProblemDetails), 403)]
[ProducesResponseType(typeof(ProblemDetails), 404)]
[ProducesResponseType(typeof(ProblemDetails), 409)]
[ProducesResponseType(typeof(ProblemDetails), 429)]
[ProducesResponseType(typeof(ProblemDetails), 502)]
public class AppBuildsController(AppBuildService service, IOptions<BaseDomainOptions> baseDomain) : ControllerBase
{
    /// <summary>The GitHub App's callback URL path, registered on the App.</summary>
    public const string CallbackPath = "/api/v4/app-builds/github/callback";

    /// <summary>The web page the callback returns to, with <c>linked</c> or <c>error</c> in its query.</summary>
    public const string WebPath = "/settings/app-builds";

    private Guid Subject => HttpContext.GetAuthContext()?.SubjectId ?? throw new UnauthorizedAccessException();

    [HttpGet, RemoteQuery]
    [ProducesResponseType(typeof(AppBuildsStatus), 200)]
    public async Task<ActionResult<AppBuildsStatus>> GetAppBuildsStatus(CancellationToken ct) =>
        Ok(await service.StatusAsync(Subject, ct));

    [HttpPost("{targetId}/link"), RemoteCommand]
    [ProducesResponseType(typeof(AppBuildLinkStart), 200)]
    public Task<ActionResult<AppBuildLinkStart>> BeginAppBuildLink(string targetId) =>
        Run(() => Task.FromResult(service.BeginLink(targetId, Subject,
            SubdomainParser.Extract(Request.Host.Host, baseDomain.Value.BaseDomain))));

    /// <summary>
    /// GitHub's redirect after the App is installed and the user authorized it. An apex hit is moved to
    /// its tenant host first (<see cref="Middleware.ApexCallbackRelayMiddleware"/>), where the session is.
    /// </summary>
    /// <remarks>
    /// <see cref="AllowAnonymousAttribute"/> only stops the framework's own 401 challenge, which would
    /// leave the browser on a bare API response. The controller's filters still run and still require
    /// the session; <see cref="RedirectRefusedCallbackAttribute"/> turns their refusal into a page.
    /// </remarks>
    [HttpGet("github/callback"), AllowAnonymous, RedirectRefusedCallback, ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> CompleteAppBuildLink(
        [FromQuery] string? code,
        [FromQuery(Name = "installation_id")] long? installationId,
        [FromQuery] string? state,
        CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        try
        {
            var target = await service.CompleteLinkAsync(code, installationId, state, Subject, ct);
            return Redirect($"{WebPath}?target={Uri.EscapeDataString(target)}&linked=success");
        }
        catch (AppBuildException ex)
        {
            return ErrorRedirect(ex.Code);
        }
    }

    internal static RedirectResult ErrorRedirect(string code) => new($"{WebPath}?error={Uri.EscapeDataString(code)}");

    [HttpGet("{targetId}/repositories"), RemoteQuery]
    [ProducesResponseType(typeof(AppBuildRepositoryOption[]), 200)]
    public Task<ActionResult<IReadOnlyList<AppBuildRepositoryOption>>> GetAppBuildRepositories(string targetId, CancellationToken ct) =>
        Run(() => service.ListRepositoriesAsync(targetId, ct));

    [HttpPut("{targetId}/repository"), RemoteCommand(Invalidates = [nameof(GetAppBuildsStatus)])]
    [ProducesResponseType(typeof(AppBuildsStatus), 200)]
    public Task<ActionResult<AppBuildsStatus>> ChooseAppBuildRepository(
        string targetId, ChooseAppBuildRepositoryRequest request, CancellationToken ct) =>
        RunThenStatus(() => service.ChooseRepositoryAsync(targetId, request.FullName, Subject, ct), ct);

    [HttpGet("{targetId}/secrets/public-key"), RemoteQuery]
    [ProducesResponseType(typeof(AppBuildSecretKey), 200)]
    public Task<ActionResult<AppBuildSecretKey>> GetAppBuildSecretKey(string targetId, CancellationToken ct) =>
        Run(() => service.GetSecretKeyAsync(targetId, ct));

    /// <summary>Relays a secret the browser sealed with <see cref="GetAppBuildSecretKey"/>. Nocturne cannot open it.</summary>
    [HttpPut("{targetId}/secrets/{name}"), RemoteCommand(Invalidates = [nameof(GetAppBuildsStatus)])]
    [ProducesResponseType(typeof(AppBuildsStatus), 200)]
    public Task<ActionResult<AppBuildsStatus>> PutAppBuildSecret(
        string targetId, string name, SealedAppBuildSecret secret, CancellationToken ct) =>
        RunThenStatus(() => service.PutSecretAsync(targetId, name, secret, Subject, ct), ct);

    [HttpPost("{targetId}/builds"), RemoteCommand(Invalidates = [nameof(GetAppBuildRuns)])]
    [ProducesResponseType(typeof(AppBuildDispatchResult), 200)]
    public Task<ActionResult<AppBuildDispatchResult>> DispatchAppBuild(
        string targetId, DispatchAppBuildRequest request, CancellationToken ct) =>
        Run(() => service.DispatchAsync(targetId, request, Subject, ct));

    [HttpGet("{targetId}/builds"), RemoteQuery]
    [ProducesResponseType(typeof(AppBuildRun[]), 200)]
    public Task<ActionResult<IReadOnlyList<AppBuildRun>>> GetAppBuildRuns(string targetId, CancellationToken ct) =>
        Run(() => service.ListRunsAsync(targetId, ct));

    [HttpDelete("{targetId}/link"), RemoteCommand(Invalidates = [nameof(GetAppBuildsStatus)])]
    [ProducesResponseType(typeof(AppBuildUnlinkResult), 200)]
    public Task<ActionResult<AppBuildUnlinkResult>> UnlinkAppBuild(string targetId, CancellationToken ct) =>
        Run(() => service.UnlinkAsync(targetId, ct));

    private Task<ActionResult<AppBuildsStatus>> RunThenStatus(Func<Task> action, CancellationToken ct) =>
        Run(async () =>
        {
            await action();
            return await service.StatusAsync(Subject, ct);
        });

    private async Task<ActionResult<T>> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (AppBuildException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Code);
        }
    }
}
