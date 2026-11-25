using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Base;

/// <summary>
/// Base controller for read-only V4 API endpoints providing list and get-by-ID operations
/// with pagination, date range filtering, device/source filtering, and sort ordering.
/// </summary>
/// <typeparam name="TModel">The V4 domain model type, must implement <see cref="IV4Record"/>.</typeparam>
/// <typeparam name="TRepository">The repository interface, must implement <see cref="IV4Repository{TModel}"/>.</typeparam>
/// <seealso cref="IV4Record"/>
/// <seealso cref="IV4Repository{TModel}"/>
/// <seealso cref="PaginatedResponse{T}"/>
/// <seealso cref="V4CrudControllerBase{TModel, TCreateRequest, TUpdateRequest, TRepository}"/>
public abstract class V4ReadOnlyControllerBase<TModel, TRepository>(TRepository repository) : ControllerBase
    where TModel : class, IV4Record
    where TRepository : IV4Repository<TModel>
{
    protected TRepository Repository { get; } = repository;

    /// <summary>
    /// Lists records with pagination, optional date range, device, and source filtering. `sort`
    /// is `timestamp_asc` (oldest first) or `timestamp_desc` (newest first, the default). `limit` is
    /// capped at a maximum page size, and a `from`/`to` range wider than the maximum span is rejected
    /// with `400 Bad Request`. `pagination.total` is returned only when `includeTotal` is `true`, or
    /// when a first page shorter than `limit` carries its own length; `includeTotal` counts the records
    /// the same filters match.
    /// </summary>
    /// <param name="from">Inclusive start of the date range filter.</param>
    /// <param name="to">Inclusive end of the date range filter.</param>
    /// <param name="limit">Maximum number of records to return. Defaults to `100`.</param>
    /// <param name="offset">Number of records to skip for pagination. Defaults to `0`.</param>
    /// <param name="sort">Sort order for results by timestamp. Defaults to `timestamp_desc`.</param>
    /// <param name="device">Optional filter to restrict results to a specific device.</param>
    /// <param name="source">Optional filter to restrict results to a specific data source.</param>
    /// <param name="includeTotal">Count every record the filters match and return it as `pagination.total`. Defaults to `false`.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The `sort` parameter accepts exactly two values:
    /// - `timestamp_asc` — oldest records first
    /// - `timestamp_desc` — newest records first (default)
    ///
    /// Use `limit` and `offset` together for paginated access to large result sets. `limit` is
    /// capped at <see cref="V4ReadLimits.MaxPageSize"/>, and a `from`/`to` range wider than
    /// <see cref="V4ReadLimits.MaxDateSpanDays"/> days is rejected with `400 Bad Request`.
    ///
    /// Counting the range is costly, so `pagination.total` is returned only when `includeTotal` is `true`.
    /// A first page shorter than `limit` carries its own length as the total without a count. Otherwise
    /// the field is absent. `includeTotal` counts the records the same filters match.
    /// </remarks>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public virtual async Task<ActionResult<PaginatedResponse<TModel>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        [FromQuery] bool includeTotal = false,
        CancellationToken ct = default)
    {
        if (PrepareListQuery(from, to, sort, ref limit, ref offset, out var descending) is { } error)
            return error;

        var data = (await Repository.GetAsync(from, to, device, source, limit, offset, descending, ct)).ToList();
        await OnBeforeReturnAsync(data, ct);
        var pagination = await PaginationInfo.ForPageAsync(limit, offset, data.Count, includeTotal, () => Repository.CountAsync(from, to, device, source, ct));
        return Ok(new PaginatedResponse<TModel> { Data = data, Pagination = pagination });
    }

    /// <summary>
    /// Validates the shared list-query parameters and clamps the paging window to
    /// <see cref="V4ReadLimits"/>.
    /// </summary>
    /// <remarks>
    /// Overrides of <see cref="GetAll"/> that add their own filters must route through this rather
    /// than re-deriving <paramref name="descending"/>, which is the only way to obtain it.
    /// </remarks>
    /// <returns>The error response to return, or <c>null</c> when the query is usable.</returns>
    protected ActionResult? PrepareListQuery(
        DateTime? from, DateTime? to, string sort, ref int limit, ref int offset, out bool descending)
    {
        if (this.PrepareListPage(sort, ref limit, ref offset, out descending) is { } invalid)
            return invalid;

        return this.RejectDateSpan(from, to);
    }

    /// <summary>Retrieves a single record by its unique identifier.</summary>
    /// <param name="id">The unique identifier of the record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>Returns `404 Not Found` if no record with the given <paramref name="id"/> exists.</remarks>
    [HttpGet("{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<TModel>> GetById(Guid id, CancellationToken ct = default)
    {
        var result = await Repository.GetByIdAsync(id, ct);
        if (result is null)
            return NotFound();

        await OnBeforeReturnAsync([result], ct);
        return Ok(result);
    }

    /// <summary>
    /// Hook called with the records an action is about to return, by every action of the record base
    /// that returns records except create, which answers with the row as stored. Override to fill
    /// fields the stored row does not carry, mutating <paramref name="records"/> in place.
    /// </summary>
    /// <remarks>
    /// An override that throws fails a read action, which has nothing committed to protect. A write
    /// action that has committed (update, bulk create, restore, bulk restore) never fails on it: it
    /// logs a warning naming the record type and ids and answers its normal success with the stored,
    /// unenriched records. The write is committed, so a failure would be reported for a write that
    /// happened. Such an action runs the hook through
    /// <see cref="V4CrudControllerBase{TModel, TCreateRequest, TUpdateRequest, TRepository}.EnrichCommittedAsync"/>.
    /// A cancelled request is not swallowed in either case.
    /// </remarks>
    /// <param name="records">The records the action returns.</param>
    /// <param name="ct">Cancellation token.</param>
    protected virtual Task OnBeforeReturnAsync(IReadOnlyCollection<TModel> records, CancellationToken ct)
        => Task.CompletedTask;
}
