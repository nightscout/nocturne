using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Devices;

/// <summary>
/// Controller for managing device event observations.
/// Exposes standard V4 CRUD operations via <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
/// </summary>
/// <remarks>
/// Device events record consumable changes and hardware lifecycle events such as
/// site changes (CAGE), sensor starts/changes (SAGE), reservoir changes (IAGE),
/// and battery changes (BAGE). These records feed the <see cref="DeviceAgeController"/>
/// calculations via <see cref="IDeviceAgeService"/>.
///
/// Create and update use the same <see cref="UpsertDeviceEventRequest"/> shape. On update,
/// the immutable fields <see cref="DeviceEvent.CorrelationId"/>, <see cref="DeviceEvent.LegacyId"/>,
/// <see cref="DeviceEvent.CreatedAt"/>, <see cref="DeviceEvent.SyncIdentifier"/>, and
/// <see cref="DeviceEvent.AdditionalProperties"/> are preserved from the existing record.
/// </remarks>
/// <seealso cref="IDeviceEventRepository"/>
/// <seealso cref="DeviceEvent"/>
/// <seealso cref="UpsertDeviceEventRequest"/>
/// <seealso cref="DeviceAgeController"/>
[ApiController]
[Route("api/v4/observations/device-events")]
[Authorize]
[Produces("application/json")]
public class DeviceEventController(IDeviceEventRepository repo)
    : V4CrudControllerBase<DeviceEvent, UpsertDeviceEventRequest, UpsertDeviceEventRequest, IDeviceEventRepository>(repo)
{
    protected override DeviceEvent MapCreateToModel(UpsertDeviceEventRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        EventType = request.EventType,
        Notes = request.Notes,
        SyncIdentifier = request.SyncIdentifier,
    };

    protected override DeviceEvent MapUpdateToModel(Guid id, UpsertDeviceEventRequest request, DeviceEvent existing) => new()
    {
        Id = id,
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        EventType = request.EventType,
        Notes = request.Notes,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        SyncIdentifier = existing.SyncIdentifier,
        AdditionalProperties = existing.AdditionalProperties,
    };

    /// <summary>
    /// Delete a device event by its external sync identifier (dataSource + syncIdentifier pair).
    /// </summary>
    [HttpDelete("by-sync-id")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> DeleteBySyncIdentifier(
        [FromQuery] string dataSource,
        [FromQuery] string syncIdentifier,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(dataSource) || string.IsNullOrEmpty(syncIdentifier))
            return BadRequest("dataSource and syncIdentifier are required");

        var deleted = await ((IDeviceEventRepository)Repository).DeleteBySyncIdentifierAsync(dataSource, syncIdentifier, ct);
        return deleted > 0 ? NoContent() : NotFound();
    }
}
