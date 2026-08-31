using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// The bodies shared by the health-record controllers, which answer alike for the same record
/// shape and differ only in the record type, the service behind it, and the scope each action is
/// gated on.
/// </summary>
/// <remarks>
/// The actions themselves stay declared on the derived controllers: NSwag names every operation
/// <c>{Controller}_{ActionMethod}</c>, and the TypeScript client method and SvelteKit remote
/// function are named from that, so an action inherited from here would rename all three.
/// </remarks>
/// <typeparam name="TModel">The domain model the controller reads and writes.</typeparam>
public abstract class HealthRecordControllerBase<TModel> : ControllerBase
{
    /// <summary>Records returned when a caller supplies no <c>count</c> and no date range.</summary>
    protected const int DefaultCount = 10;

    /// <summary>
    /// The record type as it opens a sentence — "Heart rate" — which is how it reads in an error
    /// detail. Mid-sentence uses are lower-cased from it.
    /// </summary>
    protected abstract string RecordTypeName { get; }

    protected abstract Task<IEnumerable<TModel>> ReadPageAsync(int count, int skip, CancellationToken ct);

    protected abstract Task<TModel?> ReadAsync(string id, CancellationToken ct);

    protected abstract Task<IEnumerable<TModel>> WriteManyAsync(IReadOnlyList<TModel> models, CancellationToken ct);

    protected abstract Task<TModel?> WriteAsync(string id, TModel model, CancellationToken ct);

    protected abstract Task<bool> EraseAsync(string id, CancellationToken ct);

    protected async Task<ActionResult<IEnumerable<TModel>>> ListResponseAsync(int count, int skip, CancellationToken ct) =>
        Ok(await ReadPageAsync(V4ReadLimits.ClampLimit(count), V4ReadLimits.ClampOffset(skip), ct));

    protected async Task<ActionResult<TModel>> GetResponseAsync(string id, CancellationToken ct) =>
        await ReadAsync(id, ct) is { } record ? Ok(record) : RecordNotFound(id);

    protected async Task<ActionResult<IEnumerable<TModel>>> CreateResponseAsync(
        IReadOnlyList<TModel> models, CancellationToken ct) =>
        models.Count == 0
            ? Problem(
                detail: $"At least one {char.ToLowerInvariant(RecordTypeName[0]) + RecordTypeName[1..]} record is required",
                statusCode: 400,
                title: "Bad Request")
            : Ok(await WriteManyAsync(models, ct));

    protected async Task<ActionResult<TModel>> UpdateResponseAsync(string id, TModel model, CancellationToken ct) =>
        await WriteAsync(id, model, ct) is { } updated ? Ok(updated) : RecordNotFound(id);

    protected async Task<ActionResult> DeleteResponseAsync(string id, CancellationToken ct) =>
        await EraseAsync(id, ct)
            ? Ok(new { message = $"{RecordTypeName} record deleted successfully" })
            : RecordNotFound(id);

    private ObjectResult RecordNotFound(string id) =>
        Problem(
            detail: $"{RecordTypeName} record with ID {id} not found",
            statusCode: 404,
            title: "Not Found");
}
