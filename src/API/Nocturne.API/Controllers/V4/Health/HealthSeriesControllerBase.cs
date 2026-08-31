using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// <see cref="HealthRecordControllerBase{TModel}"/> for the wearable measurement series, which
/// additionally read a date range and create from an upsert request rather than the model itself.
/// </summary>
/// <typeparam name="TModel">The domain model the controller reads and writes.</typeparam>
/// <typeparam name="TUpsertRequest">The request body a create or update carries.</typeparam>
public abstract class HealthSeriesControllerBase<TModel, TUpsertRequest> : HealthRecordControllerBase<TModel>
{
    protected abstract Task<IEnumerable<TModel>> ReadRangeAsync(
        DateTime from, DateTime to, int count, int skip, CancellationToken ct);

    protected abstract TModel ToModel(TUpsertRequest request);

    /// <remarks>
    /// A date range without a <paramref name="count"/> reads up to
    /// <see cref="V4ReadLimits.MaxPageSize"/> records rather than the whole range, so a wide range
    /// cannot load the table into memory.
    /// </remarks>
    protected async Task<ActionResult<IEnumerable<TModel>>> ListResponseAsync(
        int? count, int skip, DateTime? from, DateTime? to, CancellationToken ct)
    {
        skip = V4ReadLimits.ClampOffset(skip);

        return Ok(from.HasValue && to.HasValue
            ? await ReadRangeAsync(from.Value, to.Value, V4ReadLimits.ClampLimit(count ?? V4ReadLimits.MaxPageSize), skip, ct)
            : await ReadPageAsync(V4ReadLimits.ClampLimit(count ?? DefaultCount), skip, ct));
    }

    protected Task<ActionResult<IEnumerable<TModel>>> CreateResponseAsync(
        TUpsertRequest[] requests, CancellationToken ct) =>
        CreateResponseAsync([.. requests.Select(ToModel)], ct);
}
