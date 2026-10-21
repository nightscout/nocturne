using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Contracts.Uploads;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.TenantAdmin;

/// <summary>
/// The records uploaders sent that Nocturne answered but could not store, so the owner can see
/// what an uploader believes it saved and act on it.
/// </summary>
[ApiController]
[Tags("TenantAdmin")]
[Route("api/v4/rejected-uploads")]
public class RejectedUploadsController(IRejectedUploadStore store, IRejectedUploadRecorder recorder) : ControllerBase
{
    /// <summary>The rejected uploads, newest first, each with the record as received.</summary>
    [HttpGet]
    [RemoteQuery]
    [RequireScope(Scope.TreatmentsRead)]
    [ProducesResponseType(typeof(PaginatedResponse<RejectedUpload>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<RejectedUpload>>> GetRejectedUploads(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var page = await store.GetAsync(
            V4ReadLimits.ClampLimit(limit), V4ReadLimits.ClampOffset(offset), cancellationToken);
        return Ok(page);
    }

    /// <summary>Removes every rejected upload.</summary>
    /// <returns>How many were removed.</returns>
    [HttpDelete]
    [RemoteCommand(Invalidates = ["GetRejectedUploads"])]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public async Task<ActionResult<int>> ClearRejectedUploads(CancellationToken cancellationToken = default) =>
        Ok(await recorder.ClearAsync(cancellationToken));
}
