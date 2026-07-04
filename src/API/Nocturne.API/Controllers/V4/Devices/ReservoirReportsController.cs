using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Devices;

/// <summary>
/// Accepts manually reported reservoir values for pumps whose uploads don't carry
/// a usable level.
/// </summary>
/// <remarks>
/// Omnipod Dash only reports exact levels below 50 units, and the Ypsopump never
/// reports one — a user reading the pump screen (or an uploader sniffing a pump
/// notification) is the only source of an exact value. A report is stored as a
/// manual-source <see cref="PumpSnapshot"/> so it flows through the same alert
/// enrichment and realtime broadcast paths as uploaded device status. A
/// <see cref="ReservoirReportKind.Fill"/> report additionally records a
/// <see cref="DeviceEventType.ReservoirChange"/> device event, resetting
/// reservoir-age (IAGE) tracking.
/// </remarks>
/// <seealso cref="IPumpSnapshotRepository"/>
/// <seealso cref="IDeviceEventRepository"/>
/// <seealso cref="CreateReservoirReportRequest"/>
[ApiController]
[Tags("Devices")]
[Route("api/v4/reservoir/reports")]
[Authorize]
[Produces("application/json")]
public class ReservoirReportsController(
    IPumpSnapshotRepository pumpSnapshots,
    IDeviceEventRepository deviceEvents) : ControllerBase
{
    /// <summary>
    /// Report a known reservoir value: a spot reading of the pump's current level,
    /// or the amount just filled into a fresh reservoir/pod.
    /// </summary>
    /// <param name="request">The reservoir report.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored pump snapshot carrying the reported value.</returns>
    [HttpPost]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PumpSnapshot>> Create(
        [FromBody] CreateReservoirReportRequest request,
        CancellationToken ct = default)
    {
        var timestamp = (request.Timestamp ?? DateTimeOffset.UtcNow).UtcDateTime;

        var snapshot = await pumpSnapshots.CreateAsync(new PumpSnapshot
        {
            Timestamp = timestamp,
            UtcOffset = request.UtcOffset,
            Device = request.Device,
            App = request.App,
            DataSource = DataSources.ManualEntry,
            Reservoir = request.Units,
        }, WriteOrigin.Live, ct);

        if (request.Kind == ReservoirReportKind.Fill)
        {
            await deviceEvents.CreateAsync(new DeviceEvent
            {
                Timestamp = timestamp,
                UtcOffset = request.UtcOffset,
                Device = request.Device,
                App = request.App,
                DataSource = DataSources.ManualEntry,
                EventType = DeviceEventType.ReservoirChange,
            }, WriteOrigin.Live, ct);
        }

        return Ok(snapshot);
    }
}
