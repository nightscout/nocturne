using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Personal;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Personal;
using Nocturne.Core.Contracts.Health;
using Nocturne.Infrastructure.Data;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Personal;

[ApiController, Authorize, DenyDemoSubject, RequireScope(Scope.TenantSettings)]
[Route("api/v4/personal/google-health")]
[ProducesResponseType(typeof(ProblemDetails), 400)]
public class PersonalGoogleHealthController(IPersonalGoogleHealthService service, NocturneDbContext db) : ControllerBase
{
    private Guid Subject => HttpContext.GetAuthContext()?.SubjectId ?? throw new UnauthorizedAccessException();

    [HttpGet, RemoteQuery]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public async Task<ActionResult<GoogleHealthStatus>> GetPersonalGoogleHealth(CancellationToken ct) => Ok(await service.StatusAsync(ct));

    [HttpPut("options"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> SavePersonalGoogleHealth(GoogleHealthOptions input, CancellationToken ct) => Run(async () => await service.SaveAsync(input, Subject, ct), ct);

    [HttpPost("start"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthAuthorize), 200)]
    public async Task<ActionResult<GoogleHealthAuthorize>> StartPersonalGoogleHealth(CancellationToken ct)
    {
        try { return Ok(await service.StartAsync(Subject, ct)); }
        catch (GoogleHealthException ex) { return Problem(statusCode: 400, detail: ex.Message); }
    }

    [HttpPost("complete"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> CompletePersonalGoogleHealth(GoogleHealthCallback input, CancellationToken ct) => Run(async () => await service.CompleteAsync(input, Subject, ct), ct);

    [HttpPost("disconnect"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> DisconnectPersonalGoogleHealth(CancellationToken ct) => Run(async () => await service.DisconnectAsync(Subject, ct), ct);

    [HttpPost("sync"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public Task<ActionResult<GoogleHealthStatus>> SyncPersonalGoogleHealth(CancellationToken ct) => Run(async () => await service.QueueSyncAsync(ct), ct);

    [HttpPost("preview"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthPreview), 200)]
    public async Task<ActionResult<GoogleHealthPreview>> PreviewPersonalGoogleHealth(CancellationToken ct)
    {
        try { return Ok(await service.PreviewAsync(Subject, ct)); }
        catch (GoogleHealthException ex) { return Problem(statusCode: 400, detail: ex.Message); }
        catch (HttpRequestException) { return Problem(statusCode: 502, detail: "google_unavailable"); }
    }

    [HttpDelete("readings"), RemoteCommand, RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(GoogleHealthStatus), 200)]
    public Task<ActionResult<GoogleHealthStatus>> PurgePersonalGoogleHealth(CancellationToken ct) => Run(async () => await service.PurgeAsync(Subject, ct), ct);

    [HttpGet("readings"), RemoteQuery]
    [ProducesResponseType(typeof(List<PersonalHealthReading>), 200)]
    public async Task<ActionResult<List<PersonalHealthReading>>> GetPersonalHealthReadings(string dataType, int skip = 0, CancellationToken ct = default)
    {
        if (!GoogleHealthClient.SupportedTypes.Contains(dataType)) return BadRequest();
        return Ok(await db.PersonalHealthReadings.AsNoTracking().Where(x => x.DataType == dataType)
            .OrderByDescending(x => x.Mills).ThenBy(x => x.Id).Skip(Math.Clamp(skip, 0, 10000000)).Take(100)
            .Select(x => new PersonalHealthReading { DataType = x.DataType, Mills = x.Mills, EndMills = x.EndMills,
                Value = x.Value, Unit = x.Unit, UtcOffsetMinutes = x.UtcOffsetMinutes }).ToListAsync(ct));
    }

    private async Task<ActionResult<GoogleHealthStatus>> Run(Func<Task> action, CancellationToken ct)
    {
        try { await action(); return Ok(await service.StatusAsync(ct)); }
        catch (GoogleHealthException ex) { return Problem(statusCode: 400, detail: ex.Message); }
        catch (HttpRequestException) { return Problem(statusCode: 502, detail: "google_unavailable"); }
    }
}
