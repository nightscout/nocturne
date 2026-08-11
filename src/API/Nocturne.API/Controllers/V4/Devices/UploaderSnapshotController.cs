using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Controllers.V4.Devices;

/// <summary>
/// Controller for uploader/bridge device snapshot data.
/// Exposes standard V4 read operations via <see cref="V4ReadOnlyControllerBase{TModel,TRepository}"/>
/// plus a bulk create-or-update endpoint for native uploaders (e.g. Trio).
/// </summary>
/// <remarks>
/// Uploader snapshots capture the state of the device running the upload software
/// (e.g. phone battery, connectivity status, app version). Records are written by
/// connector ingest pipelines or uploaded directly via the bulk endpoint.
/// </remarks>
/// <seealso cref="IUploaderSnapshotRepository"/>
/// <seealso cref="UploaderSnapshot"/>
/// <seealso cref="UpsertUploaderSnapshotRequest"/>
[ApiController]
[Tags("Devices")]
[Route("api/v4/device-status/uploader")]
[RequireScope(OAuthScopes.DevicesRead)]
[Produces("application/json")]
public class UploaderSnapshotController(IUploaderSnapshotRepository repo)
    : V4ReadOnlyControllerBase<UploaderSnapshot, IUploaderSnapshotRepository>(repo)
{
    /// <summary>
    /// Create or update uploader snapshots in bulk (max 1000).
    /// </summary>
    /// <remarks>
    /// Array semantics are per-item upsert, not all-or-nothing: each snapshot carrying both
    /// `dataSource` and `syncIdentifier` updates the row already matched by that pair; all others
    /// insert. Validation failures reject the whole request with `400 Bad Request` before anything
    /// is persisted.
    ///
    /// Device attribution is a deliberate scope cut: unlike the legacy decomposer path, records
    /// written here carry no Device/PatientDevice link (snapshots are not <c>IDeviceAttributed</c>,
    /// so the stamper can't take them). Reads that join by <c>correlationId</c> are unaffected.
    /// </remarks>
    [HttpPost]
    [RequireScope(OAuthScopes.DevicesReadWrite)]
    [ProducesResponseType(typeof(UploaderSnapshot[]), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UploaderSnapshot[]>> CreateUploaderSnapshots(
        [FromBody] UpsertUploaderSnapshotRequest[] requests,
        CancellationToken ct = default)
    {
        if (requests is not { Length: > 0 })
            return Problem(detail: "Uploader snapshot data is required", statusCode: 400, title: "Bad Request");

        if (requests.Length > 1000)
            return Problem(detail: "Bulk operations are limited to 1000 snapshots per request", statusCode: 400, title: "Bad Request");

        if (requests.Any(r => r.Timestamp == default))
            return Problem(detail: "Timestamp must be set on every snapshot", statusCode: 400, title: "Bad Request");

        if (requests.Any(r => !string.IsNullOrEmpty(r.SyncIdentifier) && string.IsNullOrEmpty(r.DataSource)))
            return Problem(detail: "DataSource is required when SyncIdentifier is supplied", statusCode: 400, title: "Bad Request");

        var models = requests.Select(MapToModel).ToList();
        var persisted = await Repository.BulkUpsertAsync(models, WriteOrigin.Live, ct);
        return StatusCode(201, persisted.ToArray());
    }

    private static UploaderSnapshot MapToModel(UpsertUploaderSnapshotRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        SyncIdentifier = request.SyncIdentifier,
        CorrelationId = request.CorrelationId,
        Name = request.Name,
        Battery = request.Battery,
        BatteryVoltage = request.BatteryVoltage,
        IsCharging = request.IsCharging,
        Temperature = request.Temperature,
        Type = request.Type,
        AdditionalProperties = request.AdditionalProperties,
    };
}
