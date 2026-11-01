using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Contracts.Jobs;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>Platform-admin view of the job ledger: every scheduled job's row, failing ones included.</summary>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/jobs")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
public class JobLedgerController(IJobLedger ledger) : ControllerBase
{
    internal const int DefaultLimit = 200;
    internal const int MaxLimit = 1000;

    /// <param name="limit">How many rows to return, at most <see cref="MaxLimit"/>; failing rows come first.</param>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(JobLedgerListing), StatusCodes.Status200OK)]
    public async Task<ActionResult<JobLedgerListing>> List([FromQuery] int limit = DefaultLimit, CancellationToken ct = default) =>
        Ok(await ledger.ListAsync(Math.Clamp(V4ReadLimits.ClampLimit(limit), 1, MaxLimit), ct));
}
