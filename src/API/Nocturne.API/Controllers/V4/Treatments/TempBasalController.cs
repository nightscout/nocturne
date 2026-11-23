using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// Temporary basal insulin rates, each with a start and end time. List, read, create (one or in bulk),
/// update and delete temp basals. List deleted temp basals and restore them.
/// </summary>
/// <remarks>
/// Temp basals are span records (<see cref="TempBasal.StartTimestamp"/>/<see cref="TempBasal.EndTimestamp"/>),
/// not point events. Overlapping spans are not auto-truncated on write: the basal timeline resolves
/// overlaps at read time, matching the decomposer-written data. Only <see cref="CreateBulk"/> takes a
/// cancel, which truncates the span active at its timestamp.
///
/// On update, <see cref="TempBasal.LegacyId"/>, <see cref="TempBasal.CreatedAt"/>, <see cref="TempBasal.PumpRecordId"/>,
/// <see cref="TempBasal.DeviceId"/>, <see cref="TempBasal.InsulinContext"/> and
/// <see cref="TempBasal.AdditionalProperties"/> are preserved from the existing record, and so is
/// <see cref="TempBasal.Origin"/> unless the request supplies one. <see cref="TempBasal.PatientDeviceId"/> is
/// preserved unless <see cref="TempBasal.Device"/> changes, which attributes the span afresh.
/// <see cref="TempBasal.ScheduledRate"/> is replaced by the request like <see cref="TempBasal.Rate"/>,
/// so an update that omits it clears it.
/// <see cref="TempBasal.CorrelationId"/> falls back to the existing value if the request does not supply one.
/// </remarks>
/// <seealso cref="ITempBasalRepository"/>
/// <seealso cref="TempBasal"/>
/// <seealso cref="CreateTempBasalRequest"/>
/// <seealso cref="UpdateTempBasalRequest"/>
[ApiController]
[System.ComponentModel.DisplayName("Temp Basals")]
[Tags("Treatments")]
[Route("api/v4/insulin/temp-basals")]
[RequireScope(Scope.TreatmentsRead)]
[Produces("application/json")]
[ClearsTenantCache(TenantCacheFamily.Statistics)]
public class TempBasalController(
    ITempBasalRepository repo,
    IPatientDeviceStamper deviceStamper)
    : V4CrudControllerBase<TempBasal, CreateTempBasalRequest, UpdateTempBasalRequest, ITempBasalRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>Temp basals are treatments; the legacy equivalent is a v1 temp basal treatment.</remarks>
    public override string WriteScope => Scope.TreatmentsReadWrite;

    /// <inheritdoc/>
    protected override V4BulkNaming BulkNaming => new("Temp basal", "temp basals");

    /// <inheritdoc/>
    /// <remarks>
    /// Never cached, per <see cref="Profiles.ProfileController.GetProfileSummary"/>: a just-started
    /// or just-cancelled span must not be invisible until a cached list body expires.
    /// </remarks>
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public override Task<ActionResult<PaginatedResponse<TempBasal>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        [FromQuery] bool includeTotal = false,
        CancellationToken ct = default)
        => base.GetAll(from, to, limit, offset, sort, device, source, includeTotal, ct);

    /// <summary>
    /// Writes temp basal spans and cancels in bulk (max 1000).
    /// </summary>
    /// <param name="requests">The spans and cancels to write, at most 1000 of them.</param>
    /// <remarks>
    /// Items are processed in timestamp order, one at a time, so a batch may start a temp basal
    /// and cancel it later in the same request. Semantics are per-item, not all-or-nothing:
    ///
    /// - A regular item carrying both `dataSource` and `syncIdentifier` updates the row already
    ///   matched by that pair; all others insert.
    /// - A regular item whose pair is held by a span the owner deleted is skipped: it is not
    ///   re-created and is absent from the response, and the items around it are still written.
    ///   This holds for a one-item array too, which answers `201 Created` with an empty array
    ///   rather than `409 Conflict`. Restore the span instead to bring it back.
    /// - A cancel (`isCancel: true`) truncates the temp basal active at its timestamp by setting
    ///   the span end to that instant; with no active temp basal it is a no-op.
    ///
    /// Validation failures reject the whole request with `400 Bad Request` before anything is
    /// persisted. The response contains every record written or truncated, in processing order.
    /// </remarks>
    public override async Task<ActionResult<TempBasal[]>> CreateBulk(
        [FromBody] IReadOnlyList<CreateTempBasalRequest> requests, CancellationToken ct = default)
    {
        var naming = BulkNaming;
        if (await this.ValidateBulkAsync(requests, naming.Subject, naming.Plural, ct) is { } invalid)
            return invalid;

        var results = new List<TempBasal>(requests.Count);
        foreach (var request in requests.OrderBy(r => r.Timestamp))
        {
            if (request.IsCancel)
            {
                if (await CancelActiveAtAsync(request.Timestamp.UtcDateTime, ct) is { } truncated)
                    results.Add(truncated);
                continue;
            }

            var model = MapCreateToModel(request);
            await StampDeviceAsync(model, ct);
            try
            {
                results.Add(await Repository.CreateAsync(model, WriteOrigin.Live, ct));
            }
            catch (RecreationBlockedException)
            {
                // Skipped, per the remarks on this action.
            }
        }

        var written = results.ToArray();
        await EnrichCommittedAsync(written, ct);
        return StatusCode(StatusCodes.Status201Created, written);
    }

    /// <inheritdoc/>
    /// <remarks>A cancel is written through <see cref="CreateBulk"/>, which orders it among the spans around it.</remarks>
    protected override async Task<ObjectResult?> OnBeforeCreateAsync(
        TempBasal model, CreateTempBasalRequest request, CancellationToken ct)
    {
        if (request.IsCancel)
            return Problem(detail: "A cancel is written through the bulk endpoint", statusCode: 400, title: "Bad Request");

        await StampDeviceAsync(model, ct);
        return null;
    }

    /// <inheritdoc/>
    protected override async Task<ObjectResult?> OnBeforeUpdateAsync(
        TempBasal model, UpdateTempBasalRequest request, TempBasal existing, CancellationToken ct)
    {
        if (model.Device != existing.Device)
        {
            model.PatientDeviceId = null;
            await StampDeviceAsync(model, ct);
        }

        return null;
    }

    protected override TempBasal MapCreateToModel(CreateTempBasalRequest request) => new TempBasal
    {
        EndTimestamp = EndOf(request.Timestamp, request.DurationMinutes),
        SyncIdentifier = request.SyncIdentifier,
        CorrelationId = request.CorrelationId,
        Rate = request.Rate,
        ScheduledRate = request.ScheduledRate,
        Origin = request.Origin ?? TempBasalOriginProvenance.UploaderDefault(request.App, app: null),
        OriginEvidence = request.Origin is null ? TempBasalOriginEvidence.Uploader : TempBasalOriginEvidence.Reported,
        PumpRecordId = request.PumpRecordId,
        ApsSnapshotId = request.ApsSnapshotId,
        AdditionalProperties = request.Origin is { } origin
            ? TempBasalAutomaticFlag.Keep(null, TempBasalAutomaticFlag.Of(origin))
            : null,
    }.WithHeaderFrom(request);

    protected override TempBasal MapUpdateToModel(Guid id, UpdateTempBasalRequest request, TempBasal existing) => new TempBasal
    {
        Id = id,
        EndTimestamp = EndOf(request.Timestamp, request.DurationMinutes),
        SyncIdentifier = request.SyncIdentifier,
        CorrelationId = request.CorrelationId ?? existing.CorrelationId,
        Rate = request.Rate,
        ScheduledRate = request.ScheduledRate,
        Origin = request.Origin ?? existing.Origin,
        OriginEvidence = request.Origin is null ? existing.OriginEvidence : TempBasalOriginEvidence.Reported,
        PumpRecordId = existing.PumpRecordId,
        ApsSnapshotId = request.ApsSnapshotId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        DeviceId = existing.DeviceId,
        PatientDeviceId = existing.PatientDeviceId,
        InsulinContext = existing.InsulinContext,
        AdditionalProperties = request.Origin is { } origin
            ? TempBasalAutomaticFlag.Keep(
                existing.AdditionalProperties is null ? null : new(existing.AdditionalProperties),
                TempBasalAutomaticFlag.Of(origin))
            : existing.AdditionalProperties,
    }.WithHeaderFrom(request);

    private static DateTime? EndOf(DateTimeOffset start, double? durationMinutes)
        => durationMinutes is { } duration ? start.UtcDateTime.AddMinutes(duration) : null;

    /// <summary>
    /// Attributes the span to the patient's device like the sensor-glucose native path; otherwise
    /// direct API records stay unstamped and only ever surface as pseudo-devices.
    /// </summary>
    private Task StampDeviceAsync(TempBasal model, CancellationToken ct)
        => deviceStamper.StampAsync([model], DeviceAttributionCategories.TempBasal, model.DataSource, ct);

    /// <summary>
    /// Truncates the temp basal active at <paramref name="at"/>, or returns null when none is
    /// active (a cancel with nothing running is a no-op, matching pump behaviour).
    /// </summary>
    private async Task<TempBasal?> CancelActiveAtAsync(DateTime at, CancellationToken ct)
    {
        var active = await Repository.GetActiveAtAsync(at, ct);
        if (active is null)
            return null;

        active.EndTimestamp = at;
        return await Repository.UpdateAsync(active.Id, active, WriteOrigin.Live, ct);
    }
}
