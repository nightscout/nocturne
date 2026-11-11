using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Uploads;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.TenantAdmin;

/// <summary>
/// Records that uploaders sent, which Nocturne accepted but could not store. List these records, and clear them.
/// </summary>
/// <remarks>
/// Owner or <see cref="Scope.TenantSettings"/> only, and never a history-clamped caller: a rejected
/// upload is a whole record of any date, which the 24-hour clamp's row level security does not
/// cover because the record was never stored as data.
/// </remarks>
[ApiController]
[Tags("TenantAdmin")]
[Route("api/v4/rejected-uploads")]
[RequireScope(Scope.TenantSettings)]
public class RejectedUploadsController(IRejectedUploadStore store, IRejectedUploadRecorder recorder) : ControllerBase
{
    private const int DefaultPageSize = 25;

    private const int MaxPageSize = 100;

    /// <summary>A page of the rejected uploads, most recently seen first, each with its record.</summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(PaginatedResponse<RejectedUpload>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PaginatedResponse<RejectedUpload>>> GetRejectedUploads(
        [FromQuery] int limit = DefaultPageSize,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.IsCallerHistoryClamped())
            return Forbid();

        var page = await store.GetAsync(
            Math.Clamp(V4ReadLimits.ClampLimit(limit), 1, MaxPageSize), V4ReadLimits.ClampOffset(offset), cancellationToken);
        return Ok(page);
    }

    /// <summary>Removes the rejected uploads named in the request, which the caller has seen.</summary>
    /// <returns>How many were removed.</returns>
    [HttpPost("clear")]
    [RemoteCommand(Invalidates = ["GetRejectedUploads"])]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<int>> ClearRejectedUploads(
        [FromBody] ClearRejectedUploadsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.IsCallerHistoryClamped())
            return Forbid();

        return Ok(await recorder.ClearAsync(request.Ids, cancellationToken));
    }
}

/// <summary>The rejected uploads to remove.</summary>
public sealed record ClearRejectedUploadsRequest(IReadOnlyList<Guid> Ids);
