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

    /// <summary>Lists records with pagination, optional date range, device, and source filtering.</summary>
    /// <param name="from">Inclusive start of the date range filter.</param>
    /// <param name="to">Inclusive end of the date range filter.</param>
    /// <param name="limit">Maximum number of records to return. Defaults to `100`.</param>
    /// <param name="offset">Number of records to skip for pagination. Defaults to `0`.</param>
    /// <param name="sort">Sort order for results by timestamp. Defaults to `timestamp_desc`.</param>
    /// <param name="device">Optional filter to restrict results to a specific device.</param>
    /// <param name="source">Optional filter to restrict results to a specific data source.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The `sort` parameter accepts exactly two values:
    /// - `timestamp_asc` — oldest records first
    /// - `timestamp_desc` — newest records first (default)
    ///
    /// Use `limit` and `offset` together for paginated access to large result sets.
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
        CancellationToken ct = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        var descending = sort == "timestamp_desc";
        var data = await Repository.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await Repository.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<TModel> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
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
        return result is null ? NotFound() : Ok(result);
    }
}
