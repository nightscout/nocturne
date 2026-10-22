using System.Data.Common;
using System.Text.Json;
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
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// V4-only <see cref="ITreatmentStore"/> that reads all treatments from V4 repositories
/// via the projection service and routes writes through the decomposer.
/// </summary>
public class TreatmentReadService : ITreatmentStore
{
    private readonly IV4ToLegacyProjectionService _projection;
    private readonly ITreatmentDecomposer _decomposer;
    private readonly IDecompositionPipeline _pipeline;
    private readonly ITempBasalRepository _tempBasalRepo;
    private readonly IBolusRepository _bolusRepo;
    private readonly ICarbIntakeRepository _carbIntakeRepo;
    private readonly IBGCheckRepository _bgCheckRepo;
    private readonly INoteRepository _noteRepo;
    private readonly IDeviceEventRepository _deviceEventRepo;
    private readonly IBolusCalculationRepository _bolusCalcRepo;
    private readonly IProfileActivationRepository _activationRepo;
    private readonly IStateSpanService _stateSpans;
    private readonly ILogger<TreatmentReadService> _logger;

    public TreatmentReadService(
        IV4ToLegacyProjectionService projection,
        ITreatmentDecomposer decomposer,
        IDecompositionPipeline pipeline,
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
        _pipeline = pipeline;
        _tempBasalRepo = tempBasalRepo;
        _bolusRepo = bolusRepo;
        _carbIntakeRepo = carbIntakeRepo;
        _bgCheckRepo = bgCheckRepo;
        _noteRepo = noteRepo;
        _deviceEventRepo = deviceEventRepo;
        _bolusCalcRepo = bolusCalcRepo;
        _activationRepo = activationRepo;
        _stateSpans = stateSpans;
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
            find.FromMills, find.ToMills, limit, nativeOnly: false, descending, ct);

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
    public async Task<Treatment?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var (stored, spanId) = await ResolveAsync(id, ct);
        return (stored is null ? null : await ProjectAsync(stored.Record, ct))
            ?? await _projection.GetProjectedStateSpanTreatmentAsync(spanId, ct);
    }

    /// <inheritdoc />
    public async Task<bool> IsDeletedByUserAsync(string id, CancellationToken ct = default)
        => await _tempBasalRepo.IsDeletedByUserAsync(id, ct)
            || await _bolusRepo.IsDeletedByUserAsync(id, ct)
            || await _carbIntakeRepo.IsDeletedByUserAsync(id, ct)
            || await _bgCheckRepo.IsDeletedByUserAsync(id, ct)
            || await _deviceEventRepo.IsDeletedByUserAsync(id, ct)
            || await _bolusCalcRepo.IsDeletedByUserAsync(id, ct)
            || await _activationRepo.IsDeletedByUserAsync(id, ct)
            || await _noteRepo.IsDeletedByUserAsync(id, ct)
            || await _projection.IsStateSpanDeletedByUserAsync(id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Treatment>> GetByRangeAsync(
        long fromMills, long toMills, CancellationToken ct = default)
    {
        // Project across all V4 treatment repositories; bounds are inclusive on both ends.
        // The projection service already orders newest-first internally, but we re-sort here
        // to make the contract explicit at the read boundary.
        var projected = await _projection.GetProjectedTreatmentsAsync(
            fromMills, toMills, limit: int.MaxValue, nativeOnly: false, ct: ct);

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
    public async Task<BulkWrite<Treatment>> CreateAsync(
        IReadOnlyList<Treatment> treatments, CancellationToken ct = default, ICollection<Treatment>? stored = null)
    {
        var results = new List<Treatment>();
        var rejected = new List<BulkWriteRejection<Treatment>>();
        var skippedDeleted = 0;

        for (var i = 0; i < treatments.Count; i++)
        {
            var treatment = treatments[i];
            DecompositionResult? result = null;
            try
            {
                result = await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live, ct);
                skippedDeleted += result.SkippedDeleted;
                var created = await ToCreatedAsync(treatment, result, ct);
                results.Add(created);
                stored?.Add(created);
            }
            catch (DbUpdateException ex) when (result is null && StoreRefusal.ReasonFor(ex) is { } reason)
            {
                rejected.Add(Reject(i, treatment, ex, reason));
            }
            catch (TreatmentMappingException ex)
            {
                rejected.Add(Reject(i, treatment, ex, ex.Message));
            }
            catch (DbUpdateException ex) { throw Stopped(ex); }
            catch (DbException ex) { throw Stopped(ex); }
            catch (TimeoutException ex) { throw Stopped(ex); }
            catch (InvalidOperationException ex) { throw Stopped(ex); }
            catch (OperationCanceledException ex) { throw Stopped(ex); }

            TreatmentBatchFailedException Stopped(Exception ex)
            {
                _logger.LogError(ex, "Failed to store treatment {Id}; ending the batch", treatment.Id);
                _logger.LogSkippedDeleted(nameof(Treatment), skippedDeleted);
                var written = result is null ? results : [.. results, treatment];
                if (result is not null)
                    stored?.Add(treatment);
                return new TreatmentBatchFailedException(
                    new BulkWrite<Treatment>(written, skippedDeleted) { Rejected = rejected },
                    i, treatments.Count, failedOneStored: result is not null, ex);
            }
        }

        _logger.LogSkippedDeleted(nameof(Treatment), skippedDeleted);
        return new BulkWrite<Treatment>(results, skippedDeleted) { Rejected = rejected };
    }

    /// <summary>
    /// A treatment whose write fails the same way on every attempt is answered as refused and the
    /// batch goes on, so neither a client resending the batch nor a connector re-reading the page
    /// is held on it forever. The records written for it before the refusal stay written.
    /// </summary>
    private BulkWriteRejection<Treatment> Reject(int index, Treatment treatment, Exception ex, string reason)
    {
        _logger.LogRefused(nameof(Treatment), reason, treatment.DataSource ?? "api", ex);
        return new BulkWriteRejection<Treatment>(
            index, treatment, $"{reason}; parts of the treatment may be stored");
    }

    /// <inheritdoc />
    public async Task<Treatment?> UpdateAsync(string id, Treatment treatment, CancellationToken ct = default)
    {
        var (stored, spanId) = await ResolveAsync(id, ct);
        if (stored is null)
            return await UpdateStateSpanAsync(spanId, treatment, ct);

        if (await ProjectAsync(stored.Record, ct) is not { } existing)
            return null;

        TreatmentClientId.KeepStored(treatment, existing);

        var upsertKey = await UpsertKeyAsync(stored, ct);
        treatment.Id = upsertKey;
        await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live, ct);
        return await GetByIdAsync(upsertKey, ct)
            ?? throw ReadBackFailed(id);
    }

    private static InvalidOperationException ReadBackFailed(string id) =>
        new($"Treatment {id} did not read back after the update");

    /// <summary>
    /// Updates the state span a treatment was decomposed into, by re-decomposing the update under the
    /// treatment id the span was written under, which the decomposer upserts it on.
    /// </summary>
    private async Task<Treatment?> UpdateStateSpanAsync(string id, Treatment treatment, CancellationToken ct)
    {
        if (await _projection.GetProjectedStateSpanTreatmentAsync(id, ct) is not { Id: { } spanId }
            || await _projection.GetStateSpanTreatmentIdAsync(id, ct) is not { } upsertKey)
            return null;

        treatment.Id = upsertKey;
        await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live, ct);
        return await _projection.GetProjectedStateSpanTreatmentAsync(spanId, ct)
            ?? throw ReadBackFailed(id);
    }

    /// <inheritdoc />
    public async Task<TreatmentDeletion?> DeleteAsync(string id, CancellationToken ct = default)
    {
        var (stored, spanId) = await ResolveAsync(id, ct);
        if (stored is null)
            return await DeleteStateSpanAsync(spanId, ct);

        var served = await ProjectAsync(stored.Record, ct);

        // Every record one treatment decomposed into (a meal bolus's carb, a bolus wizard's
        // calculation, a correction's note) shares its legacy id, so deleting by it leaves no sibling
        // behind as a phantom treatment of its own.
        if (stored.Record.LegacyId is { Length: > 0 } legacyId)
        {
            var records = await _pipeline.DeleteByLegacyIdAsync<Treatment>(legacyId, WriteOrigin.Live, ct);
            var span = await _projection.GetStateSpanTreatmentIdAsync(legacyId, ct) == legacyId
                ? await DeleteStateSpanAsync(legacyId, ct)
                : null;
            return span ?? (records > 0 ? new TreatmentDeletion(served) : null);
        }

        await stored.DeleteAsync(ct);
        return new TreatmentDeletion(served);
    }

    /// <summary>
    /// Deletes a served state span with the Note its treatment wrote beside it before notes were kept
    /// on the span, which is served as part of the span and would otherwise outlive it.
    /// </summary>
    private async Task<TreatmentDeletion?> DeleteStateSpanAsync(string id, CancellationToken ct)
    {
        if (await _projection.GetProjectedStateSpanTreatmentAsync(id, ct) is not { Id: { } spanId } served
            || await _projection.GetStateSpanTreatmentIdAsync(id, ct) is not { } treatmentId)
            return null;

        await _pipeline.DeleteByLegacyIdAsync<Treatment>(treatmentId, WriteOrigin.Live, ct);
        return await _stateSpans.DeleteStateSpanAsync(spanId, ct) ? new TreatmentDeletion(served) : null;
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(string? find = null, CancellationToken ct = default)
    {
        var findQuery = FindQuery.Parse(find, FindQuery.TreatmentBareRegexFields);

        if (findQuery.HasFieldFilters)
        {
            // Field filters only exist on the projected shape; count matches within the
            // (bounded) window instead of delegating to per-repo counts.
            var projected = (await _projection.GetProjectedTreatmentsAsync(
                findQuery.FromMills, findQuery.ToMills, MaxFilterFetch, nativeOnly: false, ct: ct)).ToList();
            if (projected.Count >= MaxFilterFetch)
                _logger.LogWarning(
                    "Find-filtered treatment count hit the {MaxFetch}-row window; older matches are not counted",
                    MaxFilterFetch);
            return projected.Count(findQuery.Matches);
        }

        var (fromMills, toMills) = (findQuery.FromMills, findQuery.ToMills);
        var from = fromMills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills.Value).UtcDateTime : (DateTime?)null;
        var to = toMills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(toMills.Value).UtcDateTime : (DateTime?)null;

        var bolusCount = await _bolusRepo.CountAsync(from, to, ct);
        var carbCount = await _carbIntakeRepo.CountAsync(from, to, ct);
        var bgCheckCount = await _bgCheckRepo.CountAsync(from, to, ct);
        var noteCount = await _projection.CountNotesServedApartAsync(fromMills, toMills, ct);
        var deviceEventCount = await _deviceEventRepo.CountAsync(from, to, ct);
        var tempBasalCount = await _tempBasalRepo.CountAsync(from, to, ct);
        var bolusCalcCount = await _bolusCalcRepo.CountAsync(from, to, ct);
        var stateSpanCount = await _projection.CountProjectedStateSpanTreatmentsAsync(fromMills, toMills, ct);

        return bolusCount + carbCount + bgCheckCount + noteCount
             + deviceEventCount + tempBasalCount + bolusCalcCount + stateSpanCount;
    }

    #region Private - stored record resolution

    /// <summary>
    /// The create response for a decomposed treatment. It carries the id every read serves for the
    /// treatment, so a client that keeps the response id (Loop's objectIdCache, AAPS's nightscoutId)
    /// can edit and delete by it later; the client's own id stays stored as the records' LegacyId.
    /// A treatment that wrote none of the projected tables keeps the id it was sent with.
    /// </summary>
    private async Task<Treatment> ToCreatedAsync(Treatment treatment, DecompositionResult result, CancellationToken ct)
    {
        var written = result.CreatedRecords.Concat(result.UpdatedRecords).ToList();
        if (written.OfType<Core.Models.V4.TempBasal>().FirstOrDefault() is { } tempBasal)
            return TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        if (written.OfType<StateSpan>().FirstOrDefault(IsServedStateSpan) is { OriginalId: { } spanKey })
        {
            if (await _projection.GetProjectedStateSpanTreatmentAsync(spanKey, ct) is { Id: { } spanId })
                treatment.Id = spanId;
            return treatment;
        }

        var served = ServedRecordTypes
            .Select(type => written.OfType<IV4Record>().FirstOrDefault(type.IsInstanceOfType))
            .FirstOrDefault(record => record is not null);
        if (served is not null)
            treatment.Id = served.Id.ToString();

        return treatment;
    }

    /// <summary>
    /// Whether a state span the decomposer wrote is read back as the treatment, under the span's id.
    /// </summary>
    private static bool IsServedStateSpan(StateSpan span) =>
        span is { OriginalId: not null, Category: StateSpanCategory.Override or StateSpanCategory.TemporaryTarget };

    /// <summary>
    /// The record a decomposed treatment is read back under: a bolus heads a Meal Bolus, and a note
    /// is only its own treatment when nothing else was written.
    /// </summary>
    private static readonly Type[] ServedRecordTypes =
    [
        typeof(Bolus), typeof(CarbIntake), typeof(BGCheck), typeof(DeviceEvent), typeof(BolusCalculation),
        typeof(ProfileActivation), typeof(Note),
    ];

    /// <summary>
    /// The stored record a client-sent treatment id names, or none and the id to look a state span up
    /// by. A Note a treatment wrote beside another record is served as part of that record, so it
    /// resolves to it; see <see cref="Nocturne.API.Services.V4.LegacyTreatmentTables.NotesServedWithAnotherRecord"/>.
    /// </summary>
    private async Task<(StoredRecord? Stored, string SpanId)> ResolveAsync(string id, CancellationToken ct)
    {
        if (await FindStoredRecordAsync(id, ct) is not { } stored)
            return (null, id);

        if (stored.Record is not Note { LegacyId: { Length: > 0 } legacyId })
            return (stored, id);

        if (await FindServingRecordAsync(new RecordKey.LegacyId(legacyId), ct) is { } holder)
            return (holder, id);

        return await _projection.GetStateSpanTreatmentIdAsync(legacyId, ct) == legacyId
            ? (null, legacyId)
            : (stored, id);
    }

    /// <summary>A <see cref="ProfileActivation"/> served as a treatment: one decomposed from a treatment, so carrying its id.</summary>
    private async Task<StoredRecord?> FindServedActivationAsync(RecordKey key, CancellationToken ct) =>
        await FindAsync(_activationRepo, key, ct) is { Record.LegacyId: { Length: > 0 } } activation
            ? activation
            : null;

    /// <summary>
    /// Resolves a client-sent treatment id to the stored record it names. Each key is tried against
    /// every table before the next, looser one, so an exact match always wins over a prefix or hash
    /// match. The records of one treatment share its legacy id, so the tables are tried in the order
    /// <see cref="ToCreatedAsync"/> names a treatment by: a legacy id resolves to the record the create
    /// returned, not to the note a record treatment with notes also writes.
    /// </summary>
    private async Task<StoredRecord?> FindStoredRecordAsync(string id, CancellationToken ct)
    {
        foreach (var key in RecordKeysFor(id))
        {
            var stored = await FindServingRecordAsync(key, ct) ?? await FindAsync(_noteRepo, key, ct);
            if (stored is not null)
                return stored;
        }

        return null;
    }

    /// <summary>The stored record a key names, the Notes left out.</summary>
    private async Task<StoredRecord?> FindServingRecordAsync(RecordKey key, CancellationToken ct) =>
        await FindAsync(_tempBasalRepo, key, ct)
            ?? await FindAsync(_bolusRepo, key, ct)
            ?? await FindAsync(_carbIntakeRepo, key, ct)
            ?? await FindAsync(_bgCheckRepo, key, ct)
            ?? await FindAsync(_deviceEventRepo, key, ct)
            ?? await FindAsync(_bolusCalcRepo, key, ct)
            ?? await FindServedActivationAsync(key, ct);

    /// <summary>
    /// A UUID is a record's own id or a client's legacy id. A 24-hex id is a client's legacy id, the
    /// prefix of a record's own id (the id reads serve), or the coerced legacy id an older create
    /// echoed.
    /// </summary>
    private static IEnumerable<RecordKey> RecordKeysFor(string id)
    {
        if (Guid.TryParse(id, out var guid))
        {
            yield return new RecordKey.RecordId(guid);
            yield return new RecordKey.LegacyId(id);
            yield break;
        }

        yield return new RecordKey.LegacyId(id);

        if (MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
        {
            yield return new RecordKey.RecordIdPrefix(low, high);
            yield return new RecordKey.LegacyIdUuidPrefix(id);
            yield return new RecordKey.LegacyIdHash(id);
        }
    }

    private static async Task<StoredRecord?> FindAsync<T>(
        ILegacyKeyedRepository<T> repo, RecordKey key, CancellationToken ct) where T : class, IV4Record
    {
        var record = key switch
        {
            RecordKey.RecordId k => await repo.GetByIdAsync(k.Id, ct),
            RecordKey.LegacyId k => await repo.GetByLegacyIdAsync(k.Id, ct),
            RecordKey.RecordIdPrefix k => await repo.GetByGuidRangeAsync(k.Low, k.High, ct),
            RecordKey.LegacyIdUuidPrefix k => await repo.GetByLegacyIdUuidPrefixAsync(k.ObjectId, ct),
            RecordKey.LegacyIdHash k => await repo.GetByLegacyIdHashAsync(k.ObjectId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

        return record is null
            ? null
            : new StoredRecord(
                record,
                c => repo.DeleteAsync(record.Id, WriteOrigin.Live, c),
                c => repo.UpdateAsync(record.Id, record, WriteOrigin.Live, c));
    }

    private async Task<Treatment?> ProjectAsync(IV4Record record, CancellationToken ct)
    {
        if (record is Core.Models.V4.TempBasal tempBasal)
            return TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        // A carb intake paired into a Meal Bolus is served under the bolus's id.
        if (record is CarbIntake { CorrelationId: { } correlationId }
            && (await _bolusRepo.GetByCorrelationIdAsync(correlationId, ct)).FirstOrDefault() is { } pairedBolus)
            return await FindProjectedTreatmentAsync(pairedBolus.Mills, pairedBolus.Id.ToString(), ct);

        return await FindProjectedTreatmentAsync(record.Mills, record.Id.ToString(), ct);
    }

    /// <inheritdoc />
    public async Task<Treatment?> GetForUpdateAsync(string id, CancellationToken ct = default)
    {
        var (stored, spanId) = await ResolveAsync(id, ct);
        if (stored is null)
            return await GetStateSpanForUpdateAsync(spanId, ct);

        if (await ProjectAsync(stored.Record, ct) is not { } existing)
            return null;

        existing.Id = await UpsertKeyAsync(stored, ct);
        return existing;
    }

    private async Task<Treatment?> GetStateSpanForUpdateAsync(string id, CancellationToken ct)
    {
        if (await _projection.GetProjectedStateSpanTreatmentAsync(id, ct) is not { } existing
            || await _projection.GetStateSpanTreatmentIdAsync(id, ct) is not { } upsertKey)
            return null;

        existing.Id = upsertKey;
        return existing;
    }

    /// <summary>
    /// The LegacyId the decomposer upserts a stored record on. A native V4 row has none, so the
    /// decomposer could not match it and would insert a duplicate; it is given the id the wire
    /// already shows for it.
    /// </summary>
    private static async Task<string> UpsertKeyAsync(StoredRecord stored, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(stored.Record.LegacyId))
            return stored.Record.LegacyId;

        stored.Record.LegacyId = MongoObjectId.FromGuid(stored.Record.Id);
        await stored.SaveAsync(ct);
        return stored.Record.LegacyId;
    }

    private sealed record StoredRecord(
        IV4Record Record,
        Func<CancellationToken, Task> DeleteAsync,
        Func<CancellationToken, Task> SaveAsync);

    private abstract record RecordKey
    {
        public sealed record RecordId(Guid Id) : RecordKey;

        public sealed record LegacyId(string Id) : RecordKey;

        public sealed record RecordIdPrefix(Guid Low, Guid High) : RecordKey;

        public sealed record LegacyIdUuidPrefix(string ObjectId) : RecordKey;

        public sealed record LegacyIdHash(string ObjectId) : RecordKey;
    }

    private async Task<Treatment?> FindProjectedTreatmentAsync(
        long mills, string treatmentId, CancellationToken ct)
    {
        var projected = await _projection.GetProjectedTreatmentsAsync(
            mills, mills, 100, nativeOnly: false, ct: ct);
        return projected.FirstOrDefault(t => t.Id == treatmentId);
    }

    #endregion

}
