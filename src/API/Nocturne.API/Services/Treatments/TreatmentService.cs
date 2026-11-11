using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Helpers;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Logging;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Uploads;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// <see cref="ITreatmentService"/>, the legacy treatment module's one public face. Reads are served through <see cref="ITreatmentCache"/>
/// with fallback to <see cref="ITreatmentStore"/>; every legacy treatment write goes through here, to
/// <see cref="ITreatmentStore"/> or <see cref="ITreatmentDecomposer"/>, with event notification via
/// <see cref="IDataEventSink{T}"/>.
/// </summary>
/// <remarks>
/// On creation, bolus and basal treatments are automatically enriched with
/// <see cref="TreatmentInsulinContext"/> from the patient's configured <see cref="PatientInsulin"/>
/// records via <see cref="IPatientInsulinRepository"/>.
/// </remarks>
/// <seealso cref="ITreatmentService"/>
/// <seealso cref="ITreatmentStore"/>
/// <seealso cref="ITreatmentDecomposer"/>
/// <seealso cref="IobCalculator"/>
/// <seealso cref="CobCalculator"/>
internal class TreatmentService : ITreatmentService
{
    private const int DefaultCount = 100;
    private const int DefaultFindCount = 1000;

    private readonly ITreatmentStore _store;
    private readonly ITreatmentDecomposer _decomposer;
    private readonly ITreatmentCache _cache;
    private readonly IDataEventSink<Treatment> _events;
    private readonly IPatientInsulinRepository _insulinRepo;
    private readonly ILogger<TreatmentService> _logger;
    private readonly IRejectedUploadRecorder? _rejected;

    /// <summary>
    /// Bolus event types that should be enriched with the patient's primary bolus insulin context.
    /// </summary>
    private static readonly HashSet<string> BolusEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Snack Bolus",
        "Meal Bolus",
        "Correction Bolus",
        "Combo Bolus"
    };

    /// <summary>
    /// Basal event types that should be enriched with the patient's primary basal insulin context.
    /// </summary>
    private static readonly HashSet<string> BasalEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp Basal",
        "Temp Basal Start"
    };

    /// <summary>
    /// Initializes a new instance of <see cref="TreatmentService"/>.
    /// </summary>
    /// <param name="store">The treatment store for query and write operations.</param>
    /// <param name="decomposer">The treatment decomposer for patch and bulk delete operations.</param>
    /// <param name="cache">The treatment cache for read-through caching.</param>
    /// <param name="events">The event sink for broadcasting create/update/delete events.</param>
    /// <param name="insulinRepo">The patient insulin repository for enriching treatments with insulin context.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="rejected">Keeps the treatments a create refused; none in tests that do not look.</param>
    public TreatmentService(
        ITreatmentStore store,
        ITreatmentDecomposer decomposer,
        ITreatmentCache cache,
        IDataEventSink<Treatment> events,
        IPatientInsulinRepository insulinRepo,
        ILogger<TreatmentService> logger,
        IRejectedUploadRecorder? rejected = null)
    {
        _rejected = rejected;
        _store = store;
        _decomposer = decomposer;
        _cache = cache;
        _events = events;
        _insulinRepo = insulinRepo;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetTreatmentsAsync(
        string? find = null, int? count = null, int? skip = null,
        CancellationToken cancellationToken = default)
    {
        var query = new TreatmentQuery
        {
            Find = find,
            Count = count ?? (string.IsNullOrEmpty(find) ? DefaultCount : DefaultFindCount),
            Skip = skip ?? 0
        };

        var cached = await _cache.GetOrComputeAsync(
            query,
            () => _store.QueryAsync(query, cancellationToken),
            cancellationToken);

        return cached ?? await _store.QueryAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetTreatmentsAsync(
        int count, int skip = 0, CancellationToken cancellationToken = default)
    {
        return await GetTreatmentsAsync(null, count, skip, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Treatment?> GetTreatmentByIdAsync(
        string id, CancellationToken cancellationToken = default)
    {
        return await _store.GetByIdAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Treatment?> GetLatestTreatmentOfTypeAsync(
        string eventType, long fromMills, long toMills, CancellationToken cancellationToken = default) =>
        _store.GetLatestOfTypeAsync(eventType, fromMills, toMills, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsTreatmentDeletedByUserAsync(
        string id, CancellationToken cancellationToken = default)
        => _store.IsDeletedByUserAsync(id, cancellationToken);

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> QueryTreatmentsAsync(
        TreatmentQuery query, CancellationToken cancellationToken = default) =>
        await _store.QueryAsync(query, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Treatment>> GetTreatmentsByRangeAsync(
        long fromMills, long toMills, CancellationToken cancellationToken = default)
    {
        return await _store.GetByRangeAsync(fromMills, toMills, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> FindSameEventIdAsync(
        Treatment treatment, CancellationToken cancellationToken = default)
    {
        if (treatment.Mills <= 0)
            return null;

        return (await _store.GetByRangeAsync(treatment.Mills, treatment.Mills, cancellationToken))
            .FirstOrDefault(stored => !string.IsNullOrEmpty(stored.Id)
                && string.Equals(stored.EventType, treatment.EventType, StringComparison.Ordinal))
            ?.Id;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetTreatmentsModifiedSinceAsync(
        long lastModifiedMills, int limit = 500, CancellationToken cancellationToken = default)
    {
        return await _store.GetModifiedSinceAsync(lastModifiedMills, limit, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Before persisting, each treatment is enriched with <see cref="TreatmentInsulinContext"/>
    /// via <see cref="PopulateInsulinContextAsync"/> if its <see cref="Treatment.EventType"/>
    /// matches a known bolus or basal type and no context is already set.
    /// </remarks>
    public Task<BulkWrite<Treatment>> CreateTreatmentsAsync(
        IEnumerable<Treatment> treatments, WriteOrigin origin = WriteOrigin.Live,
        CancellationToken cancellationToken = default) =>
        CreateAsync(treatments, splitPreBolus: false, origin, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// As <see cref="CreateTreatmentsAsync"/>, with each meal split by <see cref="PreBolusSplit"/>
    /// after its insulin context is set, so the carbs take none.
    /// </remarks>
    public Task<BulkWrite<Treatment>> CreateV1TreatmentsAsync(
        IEnumerable<Treatment> treatments, WriteOrigin origin = WriteOrigin.Live,
        CancellationToken cancellationToken = default) =>
        CreateAsync(treatments, splitPreBolus: true, origin, cancellationToken);

    /// <inheritdoc />
    public async Task<DecompositionResult> ImportTreatmentsAsync(
        IReadOnlyList<Treatment> treatments, CancellationToken cancellationToken = default)
    {
        await PopulateInsulinContextAsync(treatments, cancellationToken);
        return await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Backfill, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> CountTreatmentsAsync(string? find = null, CancellationToken cancellationToken = default) =>
        _store.CountAsync(find, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, DateTime>> GetStoredIdsFromSourceAsync(
        string source, DateTime from, DateTime to, CancellationToken cancellationToken = default) =>
        _decomposer.GetLegacyIdsFromSourceAsync(source, from, to, cancellationToken);

    /// <inheritdoc />
    public Task<int> DeleteFromSourceAsync(
        string source, IReadOnlySet<string> legacyIds, CancellationToken cancellationToken = default) =>
        _decomposer.DeleteFromSourceAsync(source, legacyIds, cancellationToken);

    /// <inheritdoc />
    public Task<int> RekeyClientIdRecordsAsync(
        string source, IReadOnlyList<Treatment> treatments, CancellationToken cancellationToken = default) =>
        _decomposer.RekeyClientIdRecordsAsync(source, treatments, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Treatment>> SelectForRepublishAsync(
        string source, IReadOnlyList<Treatment> treatments, CancellationToken cancellationToken = default) =>
        _decomposer.SelectForRepublishAsync(source, treatments, cancellationToken);

    private async Task<BulkWrite<Treatment>> CreateAsync(
        IEnumerable<Treatment> treatments, bool splitPreBolus, WriteOrigin origin, CancellationToken cancellationToken)
    {
        var treatmentList = treatments.ToList();

        await PopulateInsulinContextAsync(treatmentList, cancellationToken);
        if (splitPreBolus)
            treatmentList = await SplitPreBolusAsync(treatmentList, cancellationToken);

        var stored = new List<StoredTreatment>();
        try
        {
            var created = await _store.CreateAsync(treatmentList, origin, cancellationToken, stored);
            await RecordRejectedAsync(created);
            return created;
        }
        catch (TreatmentBatchFailedException failed)
        {
            await RecordRejectedAsync(failed.Written);
            if (failed.InnerException is OperationCanceledException canceled && cancellationToken.IsCancellationRequested)
                ExceptionDispatchInfo.Throw(canceled);
            throw;
        }
        finally
        {
            // Announce what committed, whatever ended the batch.
            if (origin == WriteOrigin.Live)
                await AnnounceCreatedAsync(stored);
        }
    }

    /// <summary>
    /// Announces stored treatments as reads serve them, for the reason <see cref="PatchTreatmentAsync"/>
    /// reads its record back: a create returns the id of the record it wrote, and a carb intake paired
    /// into a meal is served under its bolus's. Treatments served as one are announced once.
    /// </summary>
    private async Task AnnounceCreatedAsync(IReadOnlyList<StoredTreatment> stored)
    {
        if (stored.Count == 0)
            return;

        var readBack = await ReadBackAsync(stored);
        var served = new List<Treatment>(stored.Count);
        for (var i = 0; i < stored.Count; i++)
        {
            var created = stored[i].Created;
            if (readBack is not null && readBack[i] is null)
                _logger.LogWarning("Created treatment {Id} did not read back", created.Id);

            var announced = readBack?[i] ?? created;
            // A state span keeps no data source, and the write-back sink tells a connector's own treatments by it.
            announced.DataSource ??= created.DataSource;
            served.Add(announced);
        }

        await _events.OnCreatedAsync([.. served.DistinctBy(t => t.Id)], CancellationToken.None);
    }

    /// <returns><c>null</c> when the read failed.</returns>
    /// <remarks>The write has committed, so a failed read-back announces the treatments as written.</remarks>
    private async Task<IReadOnlyList<Treatment?>?> ReadBackAsync(IReadOnlyList<StoredTreatment> stored)
    {
        try
        {
            return await _store.ReadBackAsync(stored, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Count} created treatments could not be read back", stored.Count);
            return null;
        }
    }

    /// <summary>
    /// <see cref="PreBolusSplit"/> over a batch. A meal whose id the user deleted keeps its carbs
    /// deleted too: its bolus is refused under that id, and its carbs would otherwise be written
    /// fresh under their own.
    /// </summary>
    private async Task<List<Treatment>> SplitPreBolusAsync(List<Treatment> treatments, CancellationToken ct)
    {
        var meals = treatments.Select(PreBolusSplit.Split).ToList();
        var split = meals.Where(halves => halves.Count == 2).Select(halves => halves[0].Id!).ToList();
        if (split.Count == 0)
            return [.. meals.SelectMany(halves => halves)];

        var deleted = await _decomposer.GetDeletedByUserAsync(split, ct);
        return [.. meals.SelectMany(halves =>
            halves.Count == 2 && deleted.Contains(halves[0].Id!) ? halves.Take(1) : halves)];
    }

    /// <summary>
    /// Keeps an update the store refused for its data as a rejected upload, as a create's refusal
    /// is kept, and returns what tells the caller it was refused.
    /// </summary>
    private async Task<TreatmentRefusedException> RefuseAsync(Treatment treatment, string reason, Exception ex)
    {
        var refusal = $"{reason}; parts of the treatment may be stored";
        _logger.LogRefused(nameof(Treatment), reason, treatment.DataSource ?? "api", ex);
        if (_rejected is not null)
            await _rejected.RecordTreatmentsAsync([new BulkWriteRejection<Treatment>(0, treatment, refusal)], CancellationToken.None);
        return new TreatmentRefusedException(refusal, ex);
    }

    private Task RecordRejectedAsync(BulkWrite<Treatment> written) =>
        _rejected is not null && written.Rejected.Count > 0
            ? _rejected.RecordTreatmentsAsync(written.Rejected, CancellationToken.None)
            : Task.CompletedTask;

    /// <inheritdoc />
    /// <returns>The updated <see cref="Treatment"/>, or <see langword="null"/> if not found.</returns>
    public async Task<Treatment?> UpdateTreatmentAsync(
        string id, Treatment treatment, CancellationToken cancellationToken = default)
    {
        Treatment? updated;
        try
        {
            updated = await _store.UpdateAsync(id, treatment, cancellationToken);
        }
        catch (DbUpdateException ex) when (StoreRefusal.ReasonFor(ex) is { } reason)
        {
            throw await RefuseAsync(treatment, reason, ex);
        }
        catch (TreatmentMappingException ex)
        {
            throw await RefuseAsync(treatment, ex.Message, ex);
        }

        if (updated is null) return null;

        await _events.OnUpdatedAsync(updated, cancellationToken);

        return updated;
    }

    /// <inheritdoc />
    /// <returns>The patched <see cref="Treatment"/>, or <see langword="null"/> if not found.</returns>
    public async Task<Treatment?> PatchTreatmentAsync(
        string id, JsonElement patchData, CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetForUpdateAsync(id, cancellationToken);
        if (existing is null) return null;

        // Apply patch fields to existing treatment
        ApplyJsonPatch(existing, patchData);

        (await _decomposer.DecomposeBatchAsync([existing], WriteOrigin.Live, cancellationToken)).ThrowIfRejected(0);

        // The merged record carries the upsert key, which the wire would coerce to a different
        // identifier than the create returned; AAPS stores the identifier from every response and
        // every pushed record, so the response and the event carry the record as reads serve it.
        // The upsert key is a stored legacy id, so it resolves exactly, with no hash scan.
        var served = await _store.GetByIdAsync(existing.Id ?? id, cancellationToken);
        if (served is null)
        {
            _logger.LogWarning("Patched treatment {Id} did not read back under its upsert key", id);
            served = existing;
        }

        await _events.OnUpdatedAsync(served, cancellationToken);

        return served;
    }

    internal static void ApplyJsonPatch(Treatment treatment, JsonElement patchData)
    {
        // The identity used to upsert (LegacyId matching) must survive the round-trip. Serializing
        // rewrites _id to its 24-hex ObjectId form, so capture the real Id and restore it after the
        // merge.
        var originalId = treatment.Id;

        JsonMergePatch.ApplyInPlace(treatment, patchData);

        // A PATCH updates the record identified by the URL; it never changes identity. Restore the
        // upsert key even if the client echoed an _id in the body (AAPS sends the derived ObjectId,
        // which would otherwise defeat the re-key and duplicate the record).
        treatment.Id = originalId;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteTreatmentAsync(
        string id, CancellationToken cancellationToken = default)
    {
        if (await _store.DeleteAsync(id, cancellationToken) is not { } deletion)
            return false;

        if (deletion.Served is not null)
            await _events.OnDeletedAsync(deletion.Served, cancellationToken);

        return true;
    }

    /// <inheritdoc />
    /// <returns>The number of treatments deleted.</returns>
    public async Task<long> DeleteTreatmentsAsync(
        string? find = null, CancellationToken cancellationToken = default)
    {
        // Field filters (eventType, enteredBy, …) don't survive the decomposer's coarse by-time
        // sweep — it would delete every record type in the window. Resolve the matching
        // treatments through the filtered read path instead and delete them individually, which
        // also removes correlated siblings (e.g. a meal bolus's carb) via LegacyId.
        if (FindQuery.Parse(find).HasFieldFilters)
            return await DeleteMatchingTreatmentsAsync(find, cancellationToken);

        return await _decomposer.BulkDeleteAsync(find, WriteOrigin.Live, cancellationToken);
    }

    private async Task<long> DeleteMatchingTreatmentsAsync(string? find, CancellationToken ct)
    {
        var matching = await _store.QueryAsync(
            new TreatmentQuery { Find = find, Count = int.MaxValue }, ct);

        long deleted = 0;
        foreach (var treatment in matching.Where(t => !string.IsNullOrEmpty(t.Id)))
        {
            if (await _store.DeleteAsync(treatment.Id!, ct) is not null)
            {
                deleted++;
                await _events.OnDeletedAsync(treatment, ct);
            }
        }

        return deleted;
    }

    /// <summary>
    /// Enriches treatments with <see cref="TreatmentInsulinContext"/> from the patient's
    /// configured <see cref="PatientInsulin"/> records. Only treatments whose
    /// <see cref="Treatment.InsulinContext"/> is <see langword="null"/> and whose
    /// <see cref="Treatment.EventType"/> matches a known bolus or basal type are enriched.
    /// </summary>
    /// <remarks>
    /// Performs at most two repository lookups (one for bolus insulin, one for basal insulin)
    /// regardless of the number of treatments, then stamps each qualifying treatment in-place.
    /// </remarks>
    /// <param name="treatments">The treatments to enrich.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task PopulateInsulinContextAsync(
        IReadOnlyList<Treatment> treatments, CancellationToken cancellationToken)
    {
        // Determine which lookups we need so we only hit the repo once per type
        var needsBolus = treatments.Any(t =>
            t.InsulinContext is null && t.EventType is not null && BolusEventTypes.Contains(t.EventType));
        var needsBasal = treatments.Any(t =>
            t.InsulinContext is null && t.EventType is not null && BasalEventTypes.Contains(t.EventType));

        PatientInsulin? bolusInsulin = null;
        PatientInsulin? basalInsulin = null;

        if (needsBolus)
            bolusInsulin = await _insulinRepo.GetPrimaryBolusInsulinAsync(cancellationToken);
        if (needsBasal)
            basalInsulin = await _insulinRepo.GetPrimaryBasalInsulinAsync(cancellationToken);

        foreach (var treatment in treatments)
        {
            if (treatment.InsulinContext is not null || treatment.EventType is null)
                continue;

            PatientInsulin? insulin = null;
            if (BolusEventTypes.Contains(treatment.EventType))
                insulin = bolusInsulin;
            else if (BasalEventTypes.Contains(treatment.EventType))
                insulin = basalInsulin;

            if (insulin is not null)
            {
                treatment.InsulinContext = new TreatmentInsulinContext
                {
                    PatientInsulinId = insulin.Id,
                    InsulinName = insulin.Name,
                    Dia = insulin.Dia,
                    Peak = insulin.Peak,
                    Curve = insulin.Curve,
                    Concentration = insulin.Concentration
                };
            }
        }
    }
}
