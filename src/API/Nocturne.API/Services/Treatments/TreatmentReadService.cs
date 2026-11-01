using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Logging;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// V4-only <see cref="ITreatmentStore"/> that reads all treatments from V4 repositories
/// via the projection service and routes writes through the decomposer.
/// </summary>
internal class TreatmentReadService : ITreatmentStore
{
    private readonly IV4ToLegacyProjectionService _projection;
    private readonly ITreatmentDecomposer _decomposer;
    private readonly LegacyTreatmentStore _store;
    private readonly ILogger<TreatmentReadService> _logger;

    public TreatmentReadService(
        IV4ToLegacyProjectionService projection,
        ITreatmentDecomposer decomposer,
        ITempBasalRepository tempBasalRepo,
        IBolusRepository bolusRepo,
        ICarbIntakeRepository carbIntakeRepo,
        IBGCheckRepository bgCheckRepo,
        INoteRepository noteRepo,
        IDeviceEventRepository deviceEventRepo,
        IBolusCalculationRepository bolusCalcRepo,
        IProfileActivationRepository activationRepo,
        IStateSpanService stateSpans,
        ILogger<TreatmentReadService> logger)
    {
        _projection = projection;
        _decomposer = decomposer;
        _store = new LegacyTreatmentStore(
            new LegacyTreatmentRepositories(
                bolusRepo, carbIntakeRepo, bgCheckRepo, noteRepo, deviceEventRepo, tempBasalRepo, bolusCalcRepo,
                activationRepo),
            projection,
            decomposer,
            stateSpans);
        _logger = logger;
    }

    /// <summary>
    /// Upper bound on rows fetched into memory when a find query carries field filters, which can
    /// only be applied after projection and therefore defeat limit pushdown.
    /// </summary>
    internal int MaxFilterFetch { get; set; } = 100_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Treatment>> QueryAsync(TreatmentQuery query, CancellationToken ct = default)
    {
        var find = FindQuery.Parse(query.Find, FindQuery.TreatmentBareRegexFields);

        var descending = !query.ReverseResults;

        if (ModifiedSearch.CursorFor(find, query.SortField, descending) is { } after)
            return await QueryModifiedAfterAsync(query, after, descending, ct);

        return find.HasFieldFilters
            ? await QueryFilteredAsync(find, query.Count, query.Skip, descending, ct)
            : await QueryByTimeRangeAsync(find, query.Count, query.Skip, descending, ct);
    }

    /// <summary>
    /// Serves a search bounded by <c>srvModified</c> from the modified-since pages, which are
    /// index-served and oldest stamp first, so a client resuming from a stamp reads only what changed.
    /// </summary>
    private async Task<IReadOnlyList<Treatment>> QueryModifiedAfterAsync(
        TreatmentQuery query, long after, bool descending, CancellationToken ct)
    {
        var live = await ModifiedSearch.CollectAsync(
            async (cursor, limit, token) =>
            {
                var page = (await _projection.GetProjectedTreatmentsModifiedSinceAsync(cursor, limit, token)).ToList();
                return new ModifiedSearch.Page<Treatment>(
                    page, page.Count == 0 ? null : page.Max(t => t.SrvModified ?? t.Mills), page.Count == 0);
            },
            treatment => treatment.IsValid == false,
            treatment => treatment.Id,
            after,
            ModifiedSearch.ArrivesInOrder(query.SortField, descending) ? (long)query.Count + query.Skip : null,
            ModifiedSearch.MaxRead,
            ct);

        Func<Treatment, long> key = query.SortField == ReadSortField.SrvModified
            ? treatment => treatment.SrvModified ?? treatment.Mills
            : treatment => treatment.Mills;

        return (descending ? live.OrderByDescending(key) : live.OrderBy(key))
            .Skip(query.Skip)
            .Take(query.Count)
            .ToList();
    }

    private async Task<IReadOnlyList<Treatment>> QueryByTimeRangeAsync(
        FindQuery find, int count, int skip, bool descending, CancellationToken ct)
    {
        var limit = (int)Math.Min((long)count + skip, int.MaxValue);
        var projected = await FetchWindowAsync(find, limit, descending, ct);

        return OrderByTime(projected, descending)
            .Skip(skip)
            .Take(count)
            .ToList();
    }

    private Task<IEnumerable<Treatment>> FetchWindowAsync(
        FindQuery find, int limit, bool descending, CancellationToken ct) =>
        _projection.GetProjectedTreatmentsAsync(
            find.FromMills, find.ToMills, limit, descending, ct);

    private static IEnumerable<Treatment> OrderByTime(IEnumerable<Treatment> treatments, bool descending) =>
        descending ? treatments.OrderByDescending(t => t.Mills) : treatments.OrderBy(t => t.Mills);

    /// <summary>
    /// Serves a find query with field filters (eventType, enteredBy, $exists, …) by matching the
    /// projected legacy shape. Paging cannot be pushed down past an in-memory filter, so the fetch
    /// window grows geometrically until the page fills or the window is exhausted.
    /// </summary>
    private async Task<IReadOnlyList<Treatment>> QueryFilteredAsync(
        FindQuery find, int count, int skip, bool descending, CancellationToken ct)
    {
        var (matching, truncated) = await FindFilteredFetch.MatchWindowAsync<Treatment>(
            async limit => (await FetchWindowAsync(find, limit, descending, ct)).ToList(),
            find.Matches, (long)count + skip, MaxFilterFetch);

        if (truncated)
            _logger.LogWarning(
                "Find-filtered treatment query hit the {MaxFetch}-row window; matches beyond it are not returned",
                MaxFilterFetch);

        if (find.HasAbandonedRegex)
            _logger.LogWarning("A find $regex ran out of its time budget; treatment matches may be incomplete");

        return OrderByTime(matching, descending)
            .Skip(skip)
            .Take(count)
            .ToList();
    }

    /// <inheritdoc />
    public Task<Treatment?> GetLatestOfTypeAsync(
        string eventType, long fromMills, long toMills, CancellationToken ct = default) =>
        _projection.GetLatestProjectedTreatmentAsync(eventType, fromMills, toMills, ct);

    /// <inheritdoc />
    public async Task<Treatment?> GetByIdAsync(string id, CancellationToken ct = default) =>
        await _store.ResolveAsync(id, ct) is { } resolved ? await resolved.ReadAsync(ct) : null;

    /// <inheritdoc />
    public Task<bool> IsDeletedByUserAsync(string id, CancellationToken ct = default) =>
        _store.IsDeletedByUserAsync(id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Treatment>> GetByRangeAsync(
        long fromMills, long toMills, CancellationToken ct = default)
    {
        // Project across all V4 treatment repositories; bounds are inclusive on both ends.
        // The projection service already orders newest-first internally, but we re-sort here
        // to make the contract explicit at the read boundary.
        var projected = await _projection.GetProjectedTreatmentsAsync(
            fromMills, toMills, limit: int.MaxValue, ct: ct);

        return projected.OrderByDescending(t => t.Mills).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Treatment>> GetModifiedSinceAsync(
        long lastModifiedMills, int limit, CancellationToken ct = default)
    {
        var projected = await _projection.GetProjectedTreatmentsModifiedSinceAsync(
            lastModifiedMills, limit, ct);

        return projected.ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// The batch is decomposed in one call (<see cref="ITreatmentDecomposer.DecomposeBatchAsync"/>); a
    /// treatment refused for its data is answered as refused and the rest are written.
    /// </remarks>
    public async Task<BulkWrite<Treatment>> CreateAsync(
        IReadOnlyList<Treatment> treatments,
        WriteOrigin origin,
        CancellationToken ct = default,
        ICollection<StoredTreatment>? stored = null)
    {
        var decomposition = new TreatmentDecomposition();
        try
        {
            await _store.KeyToServedAsync(treatments, ct);
            await _decomposer.DecomposeBatchAsync(treatments, origin, ct, decomposition);
        }
        catch (Exception ex) when (Stops(ex))
        {
            throw Stopped(ex, decomposition);
        }
        catch (Exception) when (stored is not null)
        {
            await ReportCommittedAsync(treatments, decomposition, stored);
            throw;
        }

        var results = new List<Treatment>(treatments.Count);
        var rejected = Rejections(treatments, decomposition);
        for (var i = 0; i < treatments.Count; i++)
        {
            var treatment = treatments[i];
            if (decomposition.Rejected.ContainsKey(i))
                continue;

            var of = decomposition.Of(i);
            StoredTreatment created;
            try
            {
                created = await _store.ToCreatedAsync(treatment, of, ct);
            }
            catch (Exception ex) when (Stops(ex))
            {
                _logger.LogError(ex, "Failed to read back treatment {Id}; ending the batch", treatment.Id);
                _logger.LogSkippedDeleted(nameof(Treatment), decomposition.SkippedDeleted);
                List<Treatment> unread = [treatment];
                if (Wrote(of))
                    stored?.Add(new StoredTreatment.Unnamed(treatment));
                for (var j = i + 1; j < treatments.Count; j++)
                {
                    var later = decomposition.Of(j);
                    if (later.StoredRecords.Any())
                        unread.Add(treatments[j]);
                    if (Wrote(later))
                        stored?.Add(new StoredTreatment.Unnamed(treatments[j]));
                }
                throw new TreatmentBatchFailedException(
                    new BulkWrite<Treatment>([.. results, .. unread], decomposition.SkippedDeleted) { Rejected = rejected },
                    i, treatments.Count, failedOneStored: true, ex);
            }

            results.Add(created.Created);
            if (Wrote(of))
                stored?.Add(created);
        }

        _logger.LogSkippedDeleted(nameof(Treatment), decomposition.SkippedDeleted);
        return new BulkWrite<Treatment>(results, decomposition.SkippedDeleted) { Rejected = rejected };

        TreatmentBatchFailedException Stopped(Exception ex, TreatmentDecomposition committed)
        {
            _logger.LogError(ex, "Failed to store a batch of {Count} treatments", treatments.Count);
            var written = new List<Treatment>();
            int? failedIndex = null;
            for (var i = 0; i < treatments.Count; i++)
            {
                var of = committed.Of(i);
                if (of.StoredRecords.Any())
                {
                    written.Add(treatments[i]);
                    if (Wrote(of))
                        stored?.Add(new StoredTreatment.Unnamed(treatments[i]));
                }
                else if (failedIndex is null && !committed.StoresNothing(i))
                {
                    failedIndex = i;
                }
            }

            var skippedDeleted = committed.SkippedDeleted;
            _logger.LogSkippedDeleted(nameof(Treatment), skippedDeleted);
            return new TreatmentBatchFailedException(
                new BulkWrite<Treatment>(written, skippedDeleted) { Rejected = Rejections(treatments, committed) },
                failedIndex, treatments.Count, failedOneStored: false, ex);
        }
    }

    /// <summary>
    /// Reports each treatment that wrote a record before a failure the batch does not expect, as the
    /// create would have answered it, or under the id it was sent with when that read fails too.
    /// </summary>
    private async Task ReportCommittedAsync(
        IReadOnlyList<Treatment> treatments, TreatmentDecomposition decomposition, ICollection<StoredTreatment> stored)
    {
        for (var i = 0; i < Math.Min(treatments.Count, decomposition.Groups.Count); i++)
        {
            var of = decomposition.Of(i);
            if (!Wrote(of))
                continue;
            var treatment = treatments[i];
            try
            {
                stored.Add(await _store.ToCreatedAsync(treatment, of, CancellationToken.None));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stored treatment {Id} could not be read back", treatment.Id);
                stored.Add(new StoredTreatment.Unnamed(treatment));
            }
        }
    }

    /// <summary>A failure that ends the batch, rather than one treatment's refusal.</summary>
    private static bool Stops(Exception ex) =>
        ex is DbUpdateException or DbException or TimeoutException or InvalidOperationException or OperationCanceledException;

    /// <inheritdoc />
    public Task<IReadOnlyList<Treatment?>> ReadBackAsync(
        IReadOnlyList<StoredTreatment> stored, CancellationToken ct = default) =>
        _store.ReadBackAsync(stored, ct);

    /// <summary>
    /// A treatment stored only as rows that already held what it carries is served in the response
    /// but not announced.
    /// </summary>
    private static bool Wrote(DecompositionResult result) =>
        result.CreatedRecords.Count > 0 || result.UpdatedRecords.Count > 0;

    private List<BulkWriteRejection<Treatment>> Rejections(
        IReadOnlyList<Treatment> treatments, TreatmentDecomposition decomposition) =>
        [.. decomposition.Rejected.OrderBy(r => r.Key).Select(r => Reject(r.Key, treatments[r.Key], r.Value))];

    /// <summary>
    /// A treatment whose write fails the same way on every attempt is answered as refused and the
    /// batch goes on, so neither a client resending the batch nor a connector re-reading the page
    /// is held on it forever. The records written for it before the refusal stay written.
    /// </summary>
    private BulkWriteRejection<Treatment> Reject(int index, Treatment treatment, Exception refusal)
    {
        var reason = refusal is DbUpdateException db && StoreRefusal.ReasonFor(db) is { } stored ? stored : refusal.Message;
        _logger.LogRefused(nameof(Treatment), reason, treatment.DataSource ?? "api", refusal);
        return new BulkWriteRejection<Treatment>(
            index, treatment, $"{reason}; parts of the treatment may be stored");
    }

    /// <inheritdoc />
    public async Task<Treatment?> UpdateAsync(string id, Treatment treatment, CancellationToken ct = default) =>
        await _store.ResolveAsync(id, ct) is { } resolved ? await resolved.UpdateAsync(treatment, ct) : null;

    /// <inheritdoc />
    public async Task<TreatmentDeletion?> DeleteAsync(string id, CancellationToken ct = default) =>
        await _store.ResolveAsync(id, ct) is { } resolved ? await resolved.DeleteAsync(ct) : null;

    /// <inheritdoc />
    public async Task<Treatment?> GetForUpdateAsync(string id, CancellationToken ct = default) =>
        await _store.ResolveAsync(id, ct) is { } resolved ? await resolved.ForUpdateAsync(ct) : null;

    /// <inheritdoc />
    public async Task<long> CountAsync(string? find = null, CancellationToken ct = default)
    {
        var findQuery = FindQuery.Parse(find, FindQuery.TreatmentBareRegexFields);

        if (findQuery.HasFieldFilters)
        {
            // Field filters only exist on the projected shape; count matches within the
            // (bounded) window instead of delegating to per-repo counts.
            var projected = (await _projection.GetProjectedTreatmentsAsync(
                findQuery.FromMills, findQuery.ToMills, MaxFilterFetch, ct: ct)).ToList();
            if (projected.Count >= MaxFilterFetch)
                _logger.LogWarning(
                    "Find-filtered treatment count hit the {MaxFetch}-row window; older matches are not counted",
                    MaxFilterFetch);
            return projected.Count(findQuery.Matches);
        }

        return await _projection.CountProjectedTreatmentsAsync(findQuery.FromMills, findQuery.ToMills, ct);
    }
}
