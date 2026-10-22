using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Health;
using Nocturne.Core.Contracts.Health;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Health;

[ApiController, Authorize, DenyDemoSubject, RequireScope(Scope.TenantSettings)]
[Route("api/v4/google-health")]
[ProducesResponseType(typeof(ProblemDetails), 400)]
[ProducesResponseType(typeof(ProblemDetails), 429)]
[ProducesResponseType(typeof(ProblemDetails), 502)]
public class GoogleHealthController(
    IGoogleHealthService service,
    IOptions<BaseDomainOptions> baseDomain) : ControllerBase
{
    private Guid Subject => HttpContext.GetAuthContext()?.SubjectId ?? throw new UnauthorizedAccessException();

    [HttpGet, RemoteQuery]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public async Task<ActionResult<GoogleHealthStatus>> GetGoogleHealth(CancellationToken ct) => Ok(await service.StatusAsync(ct));

    [HttpPut("options"), RemoteCommand(Invalidates = [nameof(GetGoogleHealth)])]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> SaveGoogleHealth(GoogleHealthOptions input, CancellationToken ct) =>
        RunThenStatus(() => service.SaveAsync(input, Subject, ct), ct);

    [HttpPost("start"), RemoteCommand(Invalidates = [nameof(GetGoogleHealth)])]
    [ProducesResponseType(typeof(GoogleHealthAuthorize), 200)]
    public Task<ActionResult<GoogleHealthAuthorize>> StartGoogleHealth(CancellationToken ct) =>
        Run(() => service.StartAsync(
            Subject, SubdomainParser.Extract(Request.Host.Host, baseDomain.Value.BaseDomain), ct));

    [HttpPost("complete"), RemoteCommand(Invalidates = [nameof(GetGoogleHealth)])]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> CompleteGoogleHealth(GoogleHealthCallback input, CancellationToken ct) =>
        RunThenStatus(() => service.CompleteAsync(input, Subject, ct), ct);

    [HttpPost("disconnect"), RemoteCommand(Invalidates = [nameof(GetGoogleHealth)])]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> DisconnectGoogleHealth(CancellationToken ct) =>
        RunThenStatus(() => service.DisconnectAsync(Subject, ct), ct);

    [HttpPost("sync"), RemoteCommand(Invalidates = [nameof(GetGoogleHealth)])]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> SyncGoogleHealth(CancellationToken ct) =>
        RunThenStatus(() => service.QueueSyncAsync(ct), ct);

    [HttpPost("preview"), RemoteCommand(Invalidates = [nameof(GetGoogleHealth)])]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> PreviewGoogleHealth(CancellationToken ct) =>
        Run(() => service.PreviewAsync(Subject, ct));

    [HttpDelete("readings"), RemoteCommand(Invalidates = [nameof(GetGoogleHealth)])]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> PurgeGoogleHealth(CancellationToken ct) =>
        RunThenStatus(() => service.PurgeAsync(Subject, ct), ct);

    private Task<ActionResult<GoogleHealthStatus>> RunThenStatus(Func<Task> action, CancellationToken ct) =>
        Run(async () =>
        {
            await action();
            return await service.StatusAsync(ct);
        });

    private async Task<ActionResult<T>> Run<T>(Func<Task<T>> action)
    {
        try { return Ok(await action()); }
        catch (GoogleHealthException ex)
        {
            if (ex.Message == "rate_limited" && ex.RetryAfter is { TotalSeconds: > 0 } retryAfter)
                Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Problem(
                statusCode: ex.Message switch
                {
                    "rate_limited" => StatusCodes.Status429TooManyRequests,
                    "google_unavailable" => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status400BadRequest
                }, detail: ex.Message);
        }
        catch (HttpRequestException) { return Problem(statusCode: 502, detail: "google_unavailable"); }
    }
}
