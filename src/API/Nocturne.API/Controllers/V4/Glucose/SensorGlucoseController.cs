using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Controllers.V4.Glucose;

/// <summary>
/// Controller for managing CGM sensor glucose readings.
/// Provides CRUD operations and bulk creation for <see cref="SensorGlucose"/> records.
/// After creation, evaluates glucose alerts against the canonical stream via
/// <see cref="ICanonicalAlertEvaluator"/>.
/// </summary>
/// <seealso cref="ISensorGlucoseRepository"/>
/// <seealso cref="SensorGlucose"/>
/// <seealso cref="UpsertSensorGlucoseRequest"/>
/// <seealso cref="ICanonicalAlertEvaluator"/>
/// <seealso cref="PatientDeviceAttribution"/>
/// <seealso cref="V4CrudControllerBase{TModel, TCreateRequest, TUpdateRequest, TRepository}"/>
[ApiController]
[Tags("Glucose")]
[Route("api/v4/glucose/sensor")]
[RequireScope(Scope.GlucoseRead)]
[Produces("application/json")]
public class SensorGlucoseController(
    ISensorGlucoseRepository repo,
    IGlucoseProcessingResolver glucoseResolver,
    ICanonicalAlertEvaluator alertEvaluator,
    IPatientDeviceRepository patientDevices,
    IPatientDeviceStamper deviceStamper,
    ILogger<SensorGlucoseController> logger)
    : V4CrudControllerBase<SensorGlucose, UpsertSensorGlucoseRequest, UpsertSensorGlucoseRequest, ISensorGlucoseRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>CGM readings are glucose data; the legacy equivalent is a v1 entry.</remarks>
    public override string WriteScope => Scope.GlucoseReadWrite;

    /// <inheritdoc/>
    protected override V4BulkNaming BulkNaming => new("Sensor glucose", "reading", "readings");

    /// <summary>
    /// Lists sensor glucose readings. Adds an optional <c>patientDeviceId</c> query filter on top of the base
    /// list surface: when set, results are that registered device's raw readings (canonical stream selection is
    /// bypassed); when unset, the caller sees every stored reading. Pagination totals match the base
    /// device/source behaviour (the count is unscoped by the device filters).
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
        CancellationToken ct = default)
    {
        if (PrepareListQuery(from, to, sort, ref limit, ref offset, out var descending) is { } error)
            return error;

        Guid? patientDeviceId = null;
        if (Request.Query.TryGetValue("patientDeviceId", out var raw) && Guid.TryParse(raw, out var parsed))
            patientDeviceId = parsed;

        var data = await Repository.GetAsync(from, to, device, source, limit, offset, descending,
            nativeOnly: false, afterTimestamp: null, afterId: null, patientDeviceId: patientDeviceId, ct: ct);
        var total = await Repository.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<SensorGlucose> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
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

    protected override SensorGlucose MapCreateToModel(UpsertSensorGlucoseRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Mgdl = request.Mgdl,
        Direction = request.Direction,
        TrendRate = request.TrendRate,
        Noise = request.Noise,
        Filtered = request.Filtered,
        Unfiltered = request.Unfiltered,
        Delta = request.Delta,
    };

    protected override SensorGlucose MapUpdateToModel(Guid id, UpsertSensorGlucoseRequest request, SensorGlucose existing) => new()
    {
        Id = id,
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Mgdl = request.Mgdl,
        Direction = request.Direction,
        TrendRate = request.TrendRate,
        Noise = request.Noise,
        Filtered = request.Filtered,
        Unfiltered = request.Unfiltered,
        Delta = request.Delta,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        AdditionalProperties = existing.AdditionalProperties,
    };

    /// <inheritdoc/>
    /// <remarks>
    /// Attribution is the batch form rather than a loop over <see cref="OnBeforeCreateAsync"/>: one
    /// stamper pass resolves the whole payload, and per-record DataSource drives matching, so no
    /// batch-level source is needed for a mixed-source upload.
    /// </remarks>
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

    /// <inheritdoc/>
    /// <remarks>
    /// One pass for the whole batch: alarms evaluate against the canonical stream rather than the
    /// records just written, so a per-record pass would repeat the same evaluation.
    /// </remarks>
    protected override async Task<SensorGlucose[]> OnAfterBulkCreateAsync(SensorGlucose[] written, CancellationToken ct)
    {
        if (written.Any(r => r.Mgdl > 0))
            await alertEvaluator.EvaluateAsync(ct);

        return written;
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

    protected override async Task<SensorGlucose> OnAfterCreateAsync(SensorGlucose created, CancellationToken ct)
    {
        if (created.Mgdl > 0)
            await alertEvaluator.EvaluateAsync(ct);

        return created;
    }
}
