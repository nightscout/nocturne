using Nocturne.API.Services.Alerts;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Controllers.V4.Glucose;

/// <summary>
/// Continuous glucose monitor (CGM) sensor readings. List, read, create (one or in bulk), update
/// and delete readings. List deleted readings and restore them. Glucose alerts are checked after
/// each creation.
/// </summary>
/// <remarks>
/// CRUD operations and bulk creation for <see cref="SensorGlucose"/> records. The repository announces
/// every committed write, and alerts are evaluated from that (<see cref="AlertFactsChangedSubscriber"/>).
/// </remarks>
/// <seealso cref="ISensorGlucoseRepository"/>
/// <seealso cref="SensorGlucose"/>
/// <seealso cref="UpsertSensorGlucoseRequest"/>
/// <seealso cref="PatientDeviceAttribution"/>
/// <seealso cref="V4CrudControllerBase{TModel, TCreateRequest, TUpdateRequest, TRepository}"/>
[ApiController]
[Tags("Glucose")]
[Route("api/v4/glucose/sensor")]
[RequireScope(Scope.GlucoseRead)]
[Produces("application/json")]
[ClearsTenantCache(TenantCacheFamily.Statistics, TenantCacheFamily.EHbA1c)]
public class SensorGlucoseController(
    ISensorGlucoseRepository repo,
    IGlucoseProcessingResolver glucoseResolver,
    IPatientDeviceRepository patientDevices,
    IPatientDeviceStamper deviceStamper)
    : V4CrudControllerBase<SensorGlucose, UpsertSensorGlucoseRequest, UpsertSensorGlucoseRequest, ISensorGlucoseRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>CGM readings are glucose data; the legacy equivalent is a v1 entry.</remarks>
    public override string WriteScope => Scope.GlucoseReadWrite;

    /// <inheritdoc/>
    protected override V4BulkNaming BulkNaming => new("Sensor glucose", "readings");

    /// <summary>
    /// Lists sensor glucose readings. Adds an optional <c>patientDeviceId</c> query filter on top of the base
    /// list surface: when set, results are that registered device's raw readings (canonical stream selection is
    /// bypassed); when unset, the caller sees every stored reading. Pagination totals count the
    /// records the same filters match.
    /// </summary>
    /// <remarks>
    /// The <c>patientDeviceId</c> query parameter is read directly from the request because the base list
    /// signature (shared by every V4 read controller) has no device-attribution concept — binding it here keeps
    /// a single <c>GET</c> action while adding the sensor-glucose-only filter.
    /// <para>
    /// Never cached, per <see cref="Profiles.ProfileController.GetProfileSummary"/>: a newly arrived or
    /// corrected reading must not be invisible until a cached list body expires.
    /// </para>
    /// </remarks>
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public override async Task<ActionResult<PaginatedResponse<SensorGlucose>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        [FromQuery] bool includeTotal = false,
        CancellationToken ct = default)
    {
        if (PrepareListQuery(from, to, sort, ref limit, ref offset, out var descending) is { } error)
            return error;

        Guid? patientDeviceId = null;
        if (Request.Query.TryGetValue("patientDeviceId", out var raw) && Guid.TryParse(raw, out var parsed))
            patientDeviceId = parsed;

        var data = (await Repository.GetAsync(from, to, device, source, limit, offset, descending,
            afterTimestamp: null, afterId: null, patientDeviceId: patientDeviceId, ct: ct)).ToList();
        var pagination = await PaginationInfo.ForPageAsync(limit, offset, data.Count, includeTotal, () => Repository.CountAsync(from, to, device, source, patientDeviceId, ct: ct));
        return Ok(new PaginatedResponse<SensorGlucose> { Data = data, Pagination = pagination });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V4 REST writes bypass the connector/decomposer ingest paths, so attribution happens here —
    /// otherwise direct API records stay unstamped and only ever surface as pseudo-devices. The
    /// canonical stream still governs reads.
    /// </remarks>
    protected override async Task<ObjectResult?> OnBeforeCreateAsync(
        SensorGlucose model, UpsertSensorGlucoseRequest request, CancellationToken ct)
    {
        await ResolveGlucoseAsync(model, request, ct);
        return await ApplyAttributionAsync(model, request, existing: null, ct);
    }

    /// <inheritdoc/>
    protected override async Task<ObjectResult?> OnBeforeUpdateAsync(
        SensorGlucose model, UpsertSensorGlucoseRequest request, SensorGlucose existing, CancellationToken ct)
    {
        await ResolveGlucoseAsync(model, request, ct);
        return await ApplyAttributionAsync(model, request, existing.PatientDeviceId, ct);
    }

    protected override SensorGlucose MapCreateToModel(UpsertSensorGlucoseRequest request) => new SensorGlucose
    {
        SyncIdentifier = request.SyncIdentifier,
        Mgdl = request.Mgdl,
        Direction = request.Direction,
        TrendRate = request.TrendRate,
        Noise = request.Noise,
        Filtered = request.Filtered,
        Unfiltered = request.Unfiltered,
        Delta = request.Delta,
        Rssi = request.Rssi,
    }.WithHeaderFrom(request);

    protected override SensorGlucose MapUpdateToModel(Guid id, UpsertSensorGlucoseRequest request, SensorGlucose existing) => new SensorGlucose
    {
        Id = id,
        Mgdl = request.Mgdl,
        Direction = request.Direction,
        TrendRate = request.TrendRate,
        Noise = request.Noise,
        Filtered = request.Filtered,
        Unfiltered = request.Unfiltered,
        Delta = request.Delta,
        Rssi = request.Rssi,
        SyncIdentifier = existing.SyncIdentifier,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        AdditionalProperties = existing.AdditionalProperties,
    }.WithHeaderFrom(request);

    /// <inheritdoc/>
    protected override async Task<ObjectResult?> OnBeforeBulkCreateAsync(
        IReadOnlyList<SensorGlucose> models, IReadOnlyList<UpsertSensorGlucoseRequest> requests, CancellationToken ct)
    {
        for (var i = 0; i < models.Count; i++)
            await ResolveGlucoseAsync(models[i], requests[i], ct);

        var error = await PatientDeviceAttribution.ApplyManyAsync(
            [.. models.Select((m, i) => ((IDeviceAttributed)m, requests[i].PatientDeviceId))],
            patientDevices, deviceStamper, DeviceAttributionCategories.SensorGlucose, batchSource: null, ct);

        return error is null ? null : Problem(detail: error, statusCode: 400, title: "Bad Request");
    }

    private Task ResolveGlucoseAsync(SensorGlucose model, UpsertSensorGlucoseRequest request, CancellationToken ct) =>
        glucoseResolver.ResolveAsync(model, request.GlucoseProcessing, request.SmoothedMgdl, request.UnsmoothedMgdl, ct);

    /// <summary>
    /// Settles the reading's device attribution from the request. Returns a 400 result when an explicit
    /// id doesn't resolve (tenant scoping makes a cross-tenant id indistinguishable from a nonexistent
    /// one), or <c>null</c> on success.
    /// </summary>
    private async Task<ObjectResult?> ApplyAttributionAsync(SensorGlucose model, UpsertSensorGlucoseRequest request, Guid? existing, CancellationToken ct)
    {
        var error = await PatientDeviceAttribution.ApplyAsync(
            model, request.PatientDeviceId, existing, patientDevices, deviceStamper, DeviceAttributionCategories.SensorGlucose, ct);

        return error is null ? null : Problem(detail: error, statusCode: 400, title: "Bad Request");
    }
}
