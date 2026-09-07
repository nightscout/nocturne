using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Controllers.V4.Base;

/// <summary>
/// Base controller for CRUD V4 API endpoints, extending <see cref="V4ReadOnlyControllerBase{TModel, TRepository}"/>
/// with create, update, and delete operations.
/// </summary>
/// <typeparam name="TModel">The V4 domain model type, must implement <see cref="IV4Record"/>.</typeparam>
/// <typeparam name="TCreateRequest">The request DTO type for creating records.</typeparam>
/// <typeparam name="TUpdateRequest">The request DTO type for updating records.</typeparam>
/// <typeparam name="TRepository">The repository interface, must implement <see cref="IV4Repository{TModel}"/>.</typeparam>
/// <remarks>
/// Derived controllers must implement <see cref="MapCreateToModel"/> and <see cref="MapUpdateToModel"/>
/// to map request DTOs to domain models. The <see cref="OnAfterCreateAsync"/> hook allows
/// post-creation side effects (e.g., alert evaluation).
/// Create and update methods are annotated with <see cref="RemoteFormAttribute"/>;
/// delete uses <see cref="RemoteCommandAttribute"/>.
/// Every write action is gated on <see cref="WriteScope"/>, which derived controllers must declare.
/// </remarks>
/// <seealso cref="V4ReadOnlyControllerBase{TModel, TRepository}"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="IV4Repository{TModel}"/>
/// <seealso cref="RequireDeclaredWriteScopeAttribute"/>
public abstract class V4CrudControllerBase<TModel, TCreateRequest, TUpdateRequest, TRepository>(TRepository repository)
    : V4ReadOnlyControllerBase<TModel, TRepository>(repository), IWriteScopedController
    where TModel : class, IV4Record
    where TCreateRequest : class, IBulkUpsertRequest
    where TUpdateRequest : class
    where TRepository : IV4Repository<TModel>, IBulkCreateRepository<TModel>
{
    /// <summary>
    /// The OAuth readwrite scope for this controller's data category (see <see cref="Scope"/>),
    /// required by every write action. Abstract so a new V4 CRUD controller cannot ship without
    /// declaring one: the class-level <c>[Authorize]</c> is satisfied by read-only credentials
    /// (guest links, follower and public-share grants), which must not be able to write.
    /// </summary>
    public abstract string WriteScope { get; }

    /// <summary>
    /// How <see cref="CreateBulk"/> names this controller's records when it rejects a payload.
    /// </summary>
    protected abstract V4BulkNaming BulkNaming { get; }

    /// <summary>
    /// Maps a create request DTO to the domain model.
    /// </summary>
    /// <param name="request">The create request DTO.</param>
    /// <returns>A new <typeparamref name="TModel"/> instance populated from the request.</returns>
    protected abstract TModel MapCreateToModel(TCreateRequest request);

    /// <summary>
    /// Maps an update request DTO to the domain model, preserving immutable fields from the existing record.
    /// </summary>
    /// <param name="id">The record ID being updated.</param>
    /// <param name="request">The update request DTO.</param>
    /// <param name="existing">The existing record from the database.</param>
    /// <returns>A <typeparamref name="TModel"/> instance with updated fields.</returns>
    protected abstract TModel MapUpdateToModel(Guid id, TUpdateRequest request, TModel existing);

    /// <summary>Creates a new record and returns it with a `Location` header pointing to the created resource.</summary>
    /// <param name="request">The data used to create the record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// `Timestamp` must be set on the mapped model; requests that resolve to a default timestamp are rejected with `400 Bad Request`.
    ///
    /// On success, responds with `201 Created` and a `Location` header containing the URL of the newly created record.
    ///
    /// A record whose `(dataSource, syncIdentifier)` is held by a record the owner deleted is
    /// refused with `409 Conflict` — deleting a record stops that record's source from
    /// re-uploading it. Restore it from `GET deleted` instead.
    /// </remarks>
    [HttpPost]
    [RemoteForm]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public virtual async Task<ActionResult<TModel>> Create([FromBody] TCreateRequest request, CancellationToken ct = default)
    {
        var model = MapCreateToModel(request);

        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        if (await OnBeforeCreateAsync(model, request, ct) is { } result)
            return result;

        var created = await Repository.CreateAsync(model, WriteOrigin.Live, ct);
        created = await OnAfterCreateAsync(created, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>Creates or updates many records in one request, and returns them.</summary>
    /// <param name="requests">The records to write, at most 1000 of them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Array semantics are per-item, not all-or-nothing. Of the types reachable here, boluses, basal
    /// injections and sensor glucose upsert on the sync key: an item carrying both `dataSource` and
    /// `syncIdentifier` updates in place the row already matched by that pair. Every other type —
    /// notes, device events, BG checks, calibrations, meter readings and bolus calculations — inserts,
    /// as does any item not carrying both halves of the pair.
    ///
    /// The payload is validated as a whole — an empty body, more than the cap, an item with an unset
    /// `timestamp`, an item supplying `syncIdentifier` without `dataSource`, or an item any registered
    /// validator rejects, all fail the request with `400 Bad Request` before anything is persisted.
    ///
    /// On success, responds with `201 Created` and the written records.
    /// </remarks>
    [HttpPost("bulk")]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public virtual async Task<ActionResult<TModel[]>> CreateBulk(
        [FromBody] IReadOnlyList<TCreateRequest> requests, CancellationToken ct = default)
    {
        var naming = BulkNaming;
        if (await this.ValidateBulkAsync(requests, naming.Subject, naming.Singular, naming.Plural, ct) is { } invalid)
            return invalid;

        var models = new List<TModel>(requests.Count);
        foreach (var request in requests)
            models.Add(MapCreateToModel(request));

        if (await OnBeforeBulkCreateAsync(models, requests, ct) is { } error)
            return error;

        var written = (await Repository.BulkCreateAsync(models, WriteOrigin.Live, ct)).ToArray();
        return StatusCode(StatusCodes.Status201Created, await OnAfterBulkCreateAsync(written, ct));
    }

    /// <summary>Updates an existing record by ID and returns the updated record.</summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="request">The data to apply to the existing record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Returns `404 Not Found` if no record with the given <paramref name="id"/> exists.
    ///
    /// `Timestamp` must be set on the mapped model; requests that resolve to a default timestamp are rejected with `400 Bad Request`.
    /// </remarks>
    [HttpPut("{id:guid}")]
    [RemoteForm]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<TModel>> Update(Guid id, [FromBody] TUpdateRequest request, CancellationToken ct = default)
    {
        var existing = await Repository.GetByIdAsync(id, ct);
        if (existing is null)
            return NotFound();

        var model = MapUpdateToModel(id, request, existing);

        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        if (await OnBeforeUpdateAsync(model, request, existing, ct) is { } result)
            return result;

        try
        {
            var updated = await Repository.UpdateAsync(id, model, WriteOrigin.Live, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Deletes a record by ID.</summary>
    /// <param name="id">The unique identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>Returns `204 No Content` on success, or `404 Not Found` if no record with the given <paramref name="id"/> exists.</remarks>
    [HttpDelete("{id:guid}")]
    [RemoteCommand]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        try
        {
            await Repository.DeleteAsync(id, WriteOrigin.Live, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Lists soft-deleted records available for restoration, ordered by deletion date (newest first).</summary>
    /// <param name="limit">Maximum number of records to return. Defaults to `100`.</param>
    /// <param name="offset">Number of records to skip for pagination. Defaults to `0`.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Gated on the write scope although it reads: the recycle bin exists to feed
    /// <see cref="Restore"/>, and records the owner deleted must not be enumerable by
    /// read-only credentials — in particular the anonymous public-share principal, which
    /// satisfies the class-level category read gate.
    /// </remarks>
    [HttpGet("deleted")]
    [RemoteQuery]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public virtual async Task<ActionResult<PaginatedResponse<TModel>>> ListDeleted(
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var data = await Repository.GetDeletedAsync(limit, offset, ct);
        var total = await Repository.CountDeletedAsync(ct);
        return Ok(new PaginatedResponse<TModel> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>Restores a soft-deleted record by ID.</summary>
    /// <param name="id">The unique identifier of the soft-deleted record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>Returns `200 OK` with the restored record, or `404 Not Found` if no soft-deleted record with the given <paramref name="id"/> exists.</remarks>
    [HttpPost("{id:guid}/restore")]
    [RemoteCommand]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<TModel>> Restore(Guid id, CancellationToken ct = default)
    {
        try
        {
            var restored = await Repository.RestoreAsync(id, WriteOrigin.Live, ct);
            await OnAfterRestoreAsync(restored, ct);
            return Ok(restored);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Restores multiple soft-deleted records by their IDs.</summary>
    /// <param name="ids">The unique identifiers of the soft-deleted records.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>Returns `200 OK` with the restored records. IDs that don't match a soft-deleted record are silently ignored.</remarks>
    [HttpPost("restore")]
    [RemoteCommand]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public virtual async Task<ActionResult<IEnumerable<TModel>>> BulkRestore(
        [FromBody] Guid[] ids, CancellationToken ct = default)
    {
        var restored = await Repository.BulkRestoreAsync(ids, WriteOrigin.Live, ct);
        return Ok(restored);
    }

    /// <summary>
    /// Hook called after a record is successfully created. Override to add post-creation side effects
    /// such as alert evaluation or SignalR broadcasting.
    /// </summary>
    /// <param name="created">The newly created record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The record, potentially enriched by the hook.</returns>
    protected virtual Task<TModel> OnAfterCreateAsync(TModel created, CancellationToken ct) => Task.FromResult(created);

    /// <summary>
    /// Hook called once a create request has mapped and cleared the timestamp guard, and before it
    /// persists. Override to enrich or attribute the record, mutating <paramref name="model"/> in place.
    /// </summary>
    /// <param name="model">The mapped record.</param>
    /// <param name="request">The inbound request, for the fields the domain model does not carry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>null</c> to persist, or a result that is returned to the caller verbatim in place of the
    /// write. That is usually a problem rejecting the request, but it may equally be a success
    /// result standing in for the create — an idempotent upsert answering with the record it
    /// already holds, say.
    /// </returns>
    protected virtual Task<ObjectResult?> OnBeforeCreateAsync(TModel model, TCreateRequest request, CancellationToken ct)
        => Task.FromResult<ObjectResult?>(null);

    /// <summary>
    /// Hook called once an update request has mapped and cleared the timestamp guard, and before it
    /// persists. Override to enrich or attribute the record, mutating <paramref name="model"/> in place.
    /// </summary>
    /// <param name="model">The mapped record.</param>
    /// <param name="request">The inbound request, for the fields the domain model does not carry.</param>
    /// <param name="existing">The stored record this update replaces.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>null</c> to persist, or a result that is returned to the caller verbatim in place of the
    /// write. That is usually a problem rejecting the request, but it may equally be a success
    /// result standing in for the update.
    /// </returns>
    protected virtual Task<ObjectResult?> OnBeforeUpdateAsync(TModel model, TUpdateRequest request, TModel existing, CancellationToken ct)
        => Task.FromResult<ObjectResult?>(null);

    /// <summary>
    /// Hook called once a bulk payload has mapped and before it persists. Override to enrich or
    /// attribute the batch in one pass, mutating <paramref name="models"/> in place.
    /// </summary>
    /// <remarks>
    /// An override that needs the same work <see cref="OnBeforeCreateAsync"/> does per item should
    /// call it in a loop rather than keep a second copy — unless the work has a batch form, as device
    /// attribution does in <see cref="Services.Devices.PatientDeviceAttribution.ApplyManyAsync"/>,
    /// where one pass for the payload is the point of the endpoint.
    /// </remarks>
    /// <param name="models">The mapped records, positionally aligned with <paramref name="requests"/>.</param>
    /// <param name="requests">The inbound requests, for the fields the domain model does not carry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A problem result rejecting the whole request, or <c>null</c> to persist.</returns>
    protected virtual Task<ObjectResult?> OnBeforeBulkCreateAsync(
        IReadOnlyList<TModel> models, IReadOnlyList<TCreateRequest> requests, CancellationToken ct)
        => Task.FromResult<ObjectResult?>(null);

    /// <summary>
    /// Hook called after a bulk create. Runs <see cref="OnAfterCreateAsync"/> once per written record,
    /// so a per-record side effect fires exactly as often as it would through <see cref="Create"/>.
    /// Override where the effect is batch-wide rather than per-record.
    /// </summary>
    /// <param name="written">The records as persisted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The records, potentially enriched by the hook.</returns>
    protected virtual async Task<TModel[]> OnAfterBulkCreateAsync(TModel[] written, CancellationToken ct)
    {
        for (var i = 0; i < written.Length; i++)
            written[i] = await OnAfterCreateAsync(written[i], ct);

        return written;
    }

    /// <summary>
    /// Hook called after a record is restored. Override to add post-restore side effects
    /// such as SignalR broadcasting.
    /// </summary>
    /// <param name="restored">The restored record.</param>
    /// <param name="ct">Cancellation token.</param>
    protected virtual Task OnAfterRestoreAsync(TModel restored, CancellationToken ct) => Task.CompletedTask;
}
