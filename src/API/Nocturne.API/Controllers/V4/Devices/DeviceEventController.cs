using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Controllers.V4.Devices;

/// <summary>
/// Device events for consumable and hardware changes, such as site changes, sensor starts and
/// changes, reservoir changes, and battery changes. List, read, create (one or in bulk), update and
/// delete events. List deleted events and restore them. Delete an event by its data source and sync
/// identifier.
/// </summary>
/// <remarks>
/// Standard V4 CRUD operations via <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
/// Device events record consumable changes and hardware lifecycle events such as
/// site changes (CAGE), sensor starts/changes (SAGE), reservoir changes (IAGE),
/// and battery changes (BAGE). These records feed the <see cref="DeviceAgeController"/>
/// calculations via <see cref="IDeviceAgeService"/>.
///
/// Create and update use the same <see cref="UpsertDeviceEventRequest"/> shape. On update,
/// the immutable fields <see cref="V4RecordBase.CorrelationId"/>, <see cref="V4RecordBase.LegacyId"/>,
/// <see cref="V4RecordBase.CreatedAt"/>, <see cref="DeviceEvent.SyncIdentifier"/>, and
/// <see cref="V4RecordBase.AdditionalProperties"/> are preserved from the existing record,
/// as are <see cref="DeviceEvent.DeviceId"/> and <see cref="DeviceEvent.PatientDeviceId"/>
/// when the request carries no explicit <c>patientDeviceId</c>.
/// </remarks>
/// <seealso cref="PatientDeviceAttribution"/>
/// <seealso cref="IDeviceEventRepository"/>
/// <seealso cref="DeviceEvent"/>
/// <seealso cref="UpsertDeviceEventRequest"/>
/// <seealso cref="DeviceAgeController"/>
[ApiController]
[System.ComponentModel.DisplayName("Device Events")]
[Tags("Devices")]
[Route("api/v4/observations/device-events")]
[RequireScope(Scope.DevicesRead)]
[Produces("application/json")]
[ClearsTenantCache(TenantCacheFamily.Statistics)]
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
    protected override V4BulkNaming BulkNaming => new("Device event", "events");

    /// <summary>
    /// Lists device events. Adds optional <c>patientDeviceId</c> and <c>eventType</c> filters on top of the
    /// base list surface: <c>patientDeviceId</c> keeps only events linked to that registered device, and
    /// <c>eventType</c> keeps only events of the given types (repeat the parameter for several). An unknown
    /// <c>eventType</c> or a malformed <c>patientDeviceId</c> is rejected with `400 Bad Request`.
    /// `pagination.total` counts the events the same filters match.
    /// </summary>
    /// <remarks>
    /// The base <see cref="V4ReadOnlyControllerBase{TModel, TRepository}.GetAll"/> signature is shared by
    /// every V4 read controller and cannot carry these parameters, so this overload is the routed action and
    /// the base one is withdrawn by <see cref="GetAll(DateTime?, DateTime?, int, int, string, string?, string?, bool, CancellationToken)"/>.
    /// </remarks>
    /// <param name="from">Inclusive start of the date range filter.</param>
    /// <param name="to">Inclusive end of the date range filter.</param>
    /// <param name="limit">Maximum number of records to return. Defaults to `100`.</param>
    /// <param name="offset">Number of records to skip for pagination. Defaults to `0`.</param>
    /// <param name="sort">Sort order for results by timestamp. Defaults to `timestamp_desc`.</param>
    /// <param name="device">Optional filter to restrict results to a specific device.</param>
    /// <param name="source">Optional filter to restrict results to a specific data source.</param>
    /// <param name="includeTotal">Count every record the filters match and return it as `pagination.total`. Defaults to `false`.</param>
    /// <param name="patientDeviceId">Optional filter to restrict results to events linked to this registered device.</param>
    /// <param name="eventType">Optional filter to restrict results to events of any of these types.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(PaginatedResponse<DeviceEvent>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<DeviceEvent>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        [FromQuery] bool includeTotal = false,
        [FromQuery] Guid? patientDeviceId = null,
        [FromQuery] DeviceEventType[]? eventType = null,
        CancellationToken ct = default)
    {
        if (PrepareListQuery(from, to, sort, ref limit, ref offset, out var descending) is { } error)
            return error;

        var data = (await Repository.GetAsync(from, to, device, source, limit, offset, descending,
            patientDeviceId: patientDeviceId, eventTypes: eventType, ct: ct)).ToList();
        var pagination = await PaginationInfo.ForPageAsync(limit, offset, data.Count, includeTotal,
            () => Repository.CountAsync(from, to, device, source, patientDeviceId, eventType, ct));
        return Ok(new PaginatedResponse<DeviceEvent> { Data = data, Pagination = pagination });
    }

    /// <summary>The base list action, withdrawn so the overload with the device event filters is the only route.</summary>
    [NonAction]
    public override Task<ActionResult<PaginatedResponse<DeviceEvent>>> GetAll(
        DateTime? from, DateTime? to, int limit = 100, int offset = 0, string sort = "timestamp_desc",
        string? device = null, string? source = null, bool includeTotal = false,
        CancellationToken ct = default)
        => GetAll(from, to, limit, offset, sort, device, source, includeTotal, null, null, ct);

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

    protected override DeviceEvent MapCreateToModel(UpsertDeviceEventRequest request) => new DeviceEvent
    {
        EventType = request.EventType,
        Notes = request.Notes,
        SyncIdentifier = request.SyncIdentifier,
    }.WithHeaderFrom(request);

    protected override DeviceEvent MapUpdateToModel(Guid id, UpsertDeviceEventRequest request, DeviceEvent existing) => new DeviceEvent
    {
        Id = id,
        // Preserve the legacy device link across edits; rebuilding the model without this would
        // silently drop it. PatientDeviceId is settled by ApplyAttributionAsync.
        DeviceId = existing.DeviceId,
        EventType = request.EventType,
        Notes = request.Notes,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        SyncIdentifier = existing.SyncIdentifier,
        AdditionalProperties = existing.AdditionalProperties,
    }.WithHeaderFrom(request);

    /// <inheritdoc/>
    /// <remarks>
    /// One stamper pass per distinct category list rather than one for the payload: the categories a
    /// device event can be attributed to are derived from its own event type. There is no batch-level
    /// source for the same reason as the other bulk writers — per-record DataSource drives matching.
    /// </remarks>
    protected override async Task<ObjectResult?> OnBeforeBulkCreateAsync(
        IReadOnlyList<DeviceEvent> models, IReadOnlyList<UpsertDeviceEventRequest> requests, CancellationToken ct)
    {
        // DeviceAttributionCategories.DeviceEvent hands back one of two cached lists, so this is two
        // passes however long the payload is; were it ever to allocate per call, the grouping would
        // degrade to one pass per item rather than becoming wrong.
        var byCategories = models
            .Select((model, i) => (Model: model, requests[i].PatientDeviceId))
            .GroupBy(item => DeviceAttributionCategories.DeviceEvent(item.Model.EventType));

        foreach (var group in byCategories)
        {
            var error = await PatientDeviceAttribution.ApplyManyAsync(
                [.. group.Select(item => ((IDeviceAttributed)item.Model, item.PatientDeviceId))],
                patientDevices, deviceStamper, group.Key, batchSource: null, ct);

            if (error is not null)
                return Problem(detail: error, statusCode: 400, title: "Bad Request");
        }

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
