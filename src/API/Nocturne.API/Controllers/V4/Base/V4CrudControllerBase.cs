using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

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
/// </remarks>
/// <seealso cref="V4ReadOnlyControllerBase{TModel, TRepository}"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="IV4Repository{TModel}"/>
public abstract class V4CrudControllerBase<TModel, TCreateRequest, TUpdateRequest, TRepository>(TRepository repository)
    : V4ReadOnlyControllerBase<TModel, TRepository>(repository)
    where TModel : class, IV4Record
    where TCreateRequest : class
    where TUpdateRequest : class
    where TRepository : IV4Repository<TModel>
{
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
    /// </remarks>
    [HttpPost]
    [RemoteForm]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public virtual async Task<ActionResult<TModel>> Create([FromBody] TCreateRequest request, CancellationToken ct = default)
    {
        var model = MapCreateToModel(request);

        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        var created = await Repository.CreateAsync(model, ct);
        created = await OnAfterCreateAsync(created, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<TModel>> Update(Guid id, [FromBody] TUpdateRequest request, CancellationToken ct = default)
    {
        var existing = await Repository.GetByIdAsync(id, ct);
        if (existing is null)
            return NotFound();

        var model = MapUpdateToModel(id, request, existing);

        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        try
        {
            var updated = await Repository.UpdateAsync(id, model, ct);
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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        try
        {
            await Repository.DeleteAsync(id, ct);
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
    [HttpGet("deleted")]
    [RemoteQuery]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public virtual async Task<ActionResult<PaginatedResponse<TModel>>> ListDeleted(
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<TModel>> Restore(Guid id, CancellationToken ct = default)
    {
        try
        {
            var restored = await Repository.RestoreAsync(id, ct);
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    public virtual async Task<ActionResult<IEnumerable<TModel>>> BulkRestore(
        [FromBody] Guid[] ids, CancellationToken ct = default)
    {
        var restored = await Repository.BulkRestoreAsync(ids, ct);
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
    /// Hook called after a record is restored. Override to add post-restore side effects
    /// such as SignalR broadcasting.
    /// </summary>
    /// <param name="restored">The restored record.</param>
    /// <param name="ct">Cancellation token.</param>
    protected virtual Task OnAfterRestoreAsync(TModel restored, CancellationToken ct) => Task.CompletedTask;
}
