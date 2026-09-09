using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
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
public class GoogleHealthController(IGoogleHealthService service) : ControllerBase
{
    private Guid Subject => HttpContext.GetAuthContext()?.SubjectId ?? throw new UnauthorizedAccessException();

    [HttpGet, RemoteQuery]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public async Task<ActionResult<GoogleHealthStatus>> GetGoogleHealth(CancellationToken ct) => Ok(await service.StatusAsync(ct));

    [HttpPut("options"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> SaveGoogleHealth(GoogleHealthOptions input, CancellationToken ct) => Run(async () => await service.SaveAsync(input, Subject, ct), ct);

    [HttpPost("start"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthAuthorize), 200)]
    public async Task<ActionResult<GoogleHealthAuthorize>> StartGoogleHealth(CancellationToken ct)
    {
        try { return Ok(await service.StartAsync(Subject, ct)); }
        catch (GoogleHealthException ex) { return Problem(statusCode: 400, detail: ex.Message); }
    }

    [HttpPost("complete"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> CompleteGoogleHealth(GoogleHealthCallback input, CancellationToken ct) => Run(async () => await service.CompleteAsync(input, Subject, ct), ct);

    [HttpPost("disconnect"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> DisconnectGoogleHealth(CancellationToken ct) => Run(async () => await service.DisconnectAsync(Subject, ct), ct);

    [HttpPost("sync"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public Task<ActionResult<GoogleHealthStatus>> SyncGoogleHealth(CancellationToken ct) => Run(async () => await service.QueueSyncAsync(ct), ct);

    [HttpPost("preview"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthPreview), 200)]
    public async Task<ActionResult<GoogleHealthPreview>> PreviewGoogleHealth(CancellationToken ct)
    {
        try { return Ok(await service.PreviewAsync(Subject, ct)); }
        catch (GoogleHealthException ex) { return Problem(statusCode: 400, detail: ex.Message); }
        catch (HttpRequestException) { return Problem(statusCode: 502, detail: "google_unavailable"); }
    }

    [HttpDelete("readings"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> PurgeGoogleHealth(CancellationToken ct) => Run(async () => await service.PurgeAsync(Subject, ct), ct);

    private async Task<ActionResult<GoogleHealthStatus>> Run(Func<Task> action, CancellationToken ct)
    {
        try { await action(); return Ok(await service.StatusAsync(ct)); }
        catch (GoogleHealthException ex) { return Problem(statusCode: 400, detail: ex.Message); }
        catch (HttpRequestException) { return Problem(statusCode: 502, detail: "google_unavailable"); }
    }
}
