using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Devices;

/// <summary>
/// The battery status a CGM transmitter last reported through its uploader.
/// </summary>
/// <seealso cref="TransmitterStatus"/>
[ApiController]
[Tags("Devices")]
[Route("api/v4/device-status/transmitter")]
[RequireScope(Scope.DevicesRead)]
[Produces("application/json")]
public class TransmitterStatusController(IUploaderSnapshotRepository uploaderSnapshots) : ControllerBase
{
    /// <summary>
    /// Get the latest transmitter battery status.
    /// </summary>
    /// <returns>The status, or no content when the caller can read none.</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(TransmitterStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<TransmitterStatus>> GetTransmitterStatus(CancellationToken ct = default)
    {
        var latest = await uploaderSnapshots.GetLatestOfTypeAsync(TransmitterStatus.UploaderType, asOf: null, ct);
        return TransmitterStatus.From(latest) is { } status ? Ok(status) : NoContent();
    }
}
