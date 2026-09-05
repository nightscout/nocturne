using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

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
/// <see cref="DeviceEvent.AdditionalProperties"/> are preserved from the existing record,
/// as are <see cref="DeviceEvent.DeviceId"/> and <see cref="DeviceEvent.PatientDeviceId"/>
/// when the request carries no explicit <c>patientDeviceId</c>.
/// </remarks>
/// <seealso cref="PatientDeviceAttribution"/>
/// <seealso cref="IDeviceEventRepository"/>
/// <seealso cref="DeviceEvent"/>
/// <seealso cref="UpsertDeviceEventRequest"/>
/// <seealso cref="DeviceAgeController"/>
[ApiController]
[Route("api/v4/observations/device-events")]
[RequireScope(Scope.DevicesRead)]
[Produces("application/json")]
public class DeviceEventController(
    IDeviceEventRepository repo,
    IPatientDeviceRepository patientDevices,
    IPatientDeviceStamper deviceStamper)
    : V4CrudControllerBase<DeviceEvent, UpsertDeviceEventRequest, UpsertDeviceEventRequest, IDeviceEventRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>
    /// Device events record hardware lifecycle (site/sensor/reservoir/battery changes) and sit under
    /// the <c>devices.read</c> share category, so they follow devices rather than the treatments
    /// category their legacy event types came from.
    /// </remarks>
    public override string WriteScope => Scope.DevicesReadWrite;

    /// <inheritdoc/>
    protected override V4BulkNaming BulkNaming => new("Device event", "event", "events");

    /// <summary>
    /// Lists device events. Adds an optional <c>patientDeviceId</c> query filter on top of the base list
    /// surface: when set, only events linked to that registered device are returned. Pagination totals
    /// match the base device/source behaviour (the count is unscoped by the device filters).
    /// </summary>
    /// <remarks>
    /// The <c>patientDeviceId</c> query parameter is read directly from the request because the base list
    /// signature (shared by every V4 read controller) has no device-attribution concept — binding it here
    /// keeps a single <c>GET</c> action while adding the device-event-only filter.
    /// </remarks>
    public override async Task<ActionResult<PaginatedResponse<DeviceEvent>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        if (PrepareListQuery(from, to, sort, ref limit, ref offset, out var descending) is { } error)
            return error;

        Guid? patientDeviceId = null;
        if (Request.Query.TryGetValue("patientDeviceId", out var raw) && Guid.TryParse(raw, out var parsed))
            patientDeviceId = parsed;

        var data = await Repository.GetAsync(from, to, device, source, limit, offset, descending,
            nativeOnly: false, patientDeviceId: patientDeviceId, ct: ct);
        var total = await Repository.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<DeviceEvent> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V4 REST writes bypass the connector/decomposer ingest paths, so attribution happens here —
    /// otherwise direct API records stay unstamped.
    /// </remarks>
    protected override Task<ObjectResult?> OnBeforeCreateAsync(
        DeviceEvent model, UpsertDeviceEventRequest request, CancellationToken ct)
        => ApplyAttributionAsync(model, request, existing: null, ct);

    /// <inheritdoc/>
    protected override Task<ObjectResult?> OnBeforeUpdateAsync(
        DeviceEvent model, UpsertDeviceEventRequest request, DeviceEvent existing, CancellationToken ct)
        => ApplyAttributionAsync(model, request, existing.PatientDeviceId, ct);

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
        // Preserve the legacy device link across edits; rebuilding the model without this would
        // silently drop it. PatientDeviceId is settled by ApplyAttributionAsync.
        DeviceId = existing.DeviceId,
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

    /// <inheritdoc/>
    /// <remarks>
    /// One item at a time rather than a single stamper pass: the device categories a device event can
    /// be attributed to are derived from its own event type, so a batch has no one category list to
    /// share.
    /// </remarks>
    protected override async Task<ObjectResult?> OnBeforeBulkCreateAsync(
        IReadOnlyList<DeviceEvent> models, IReadOnlyList<UpsertDeviceEventRequest> requests, CancellationToken ct)
    {
        for (var i = 0; i < models.Count; i++)
            if (await OnBeforeCreateAsync(models[i], requests[i], ct) is { } error)
                return error;

        return null;
    }

    /// <summary>
    /// Settles the event's device attribution from the request. Returns a 400 result when an explicit
    /// id doesn't resolve (tenant scoping makes a cross-tenant id indistinguishable from a nonexistent
    /// one), or <c>null</c> on success.
    /// </summary>
    private async Task<ObjectResult?> ApplyAttributionAsync(DeviceEvent model, UpsertDeviceEventRequest request, Guid? existing, CancellationToken ct)
    {
        var error = await PatientDeviceAttribution.ApplyAsync(
            model, request.PatientDeviceId, existing, patientDevices, deviceStamper, DeviceAttributionCategories.DeviceEvent(model.EventType), ct);

        return error is null ? null : Problem(detail: error, statusCode: 400, title: "Bad Request");
    }

    /// <summary>
    /// Delete a device event by its external sync identifier (dataSource + syncIdentifier pair).
    /// </summary>
    [HttpDelete("by-sync-id")]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> DeleteBySyncIdentifier(
        [FromQuery] string dataSource,
        [FromQuery] string syncIdentifier,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(dataSource) || string.IsNullOrEmpty(syncIdentifier))
            return BadRequest("dataSource and syncIdentifier are required");

        var deleted = await ((IDeviceEventRepository)Repository).DeleteBySyncIdentifierAsync(dataSource, syncIdentifier, WriteOrigin.Live, ct);
        return deleted > 0 ? NoContent() : NotFound();
    }
}
