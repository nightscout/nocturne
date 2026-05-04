using System.Text.Json;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// Domain service implementation for <see cref="Treatment"/> operations using Store/Cache/EventSink ports.
/// Reads are served through <see cref="ITreatmentCache"/> with fallback to <see cref="ITreatmentStore"/>,
/// writes go through <see cref="ITreatmentStore"/> or <see cref="ITreatmentDecomposer"/> with event
/// notification via <see cref="IDataEventSink{T}"/>.
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
public class TreatmentService : ITreatmentService
{
    private readonly ITreatmentStore _store;
    private readonly ITreatmentDecomposer _decomposer;
    private readonly ITreatmentCache _cache;
    private readonly IDataEventSink<Treatment> _events;
    private readonly IPatientInsulinRepository _insulinRepo;
    private readonly ILogger<TreatmentService> _logger;

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
    public TreatmentService(
        ITreatmentStore store,
        ITreatmentDecomposer decomposer,
        ITreatmentCache cache,
        IDataEventSink<Treatment> events,
        IPatientInsulinRepository insulinRepo,
        ILogger<TreatmentService> logger)
    {
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
            Count = count ?? 10,
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
    public async Task<IEnumerable<Treatment>> GetTreatmentsWithAdvancedFilterAsync(
        int count, int skip, string? findQuery, bool reverseResults,
        CancellationToken cancellationToken = default)
    {
        var query = new TreatmentQuery
        {
            Find = findQuery,
            Count = count,
            Skip = skip,
            ReverseResults = reverseResults
        };

        return await _store.QueryAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Treatment>> GetTreatmentsByRangeAsync(
        long fromMills, long toMills, CancellationToken cancellationToken = default)
    {
        return await _store.GetByRangeAsync(fromMills, toMills, cancellationToken);
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
    /// matches a known bolus or basal type and no context is already set. After creation,
    /// the <see cref="ITreatmentCache"/> is invalidated and events are fired via
    /// <see cref="IDataEventSink{T}.OnCreatedAsync(IReadOnlyList{T}, CancellationToken)"/>.
    /// </remarks>
    public async Task<IEnumerable<Treatment>> CreateTreatmentsAsync(
        IEnumerable<Treatment> treatments, CancellationToken cancellationToken = default)
    {
        var treatmentList = treatments.ToList();

        await PopulateInsulinContextAsync(treatmentList, cancellationToken);

        var created = await _store.CreateAsync(treatmentList, cancellationToken);

        await _cache.InvalidateAsync(cancellationToken);
        await _events.OnCreatedAsync(created, cancellationToken);

        return created;
    }

    /// <inheritdoc />
    /// <returns>The updated <see cref="Treatment"/>, or <see langword="null"/> if not found.</returns>
    public async Task<Treatment?> UpdateTreatmentAsync(
        string id, Treatment treatment, CancellationToken cancellationToken = default)
    {
        var updated = await _store.UpdateAsync(id, treatment, cancellationToken);
        if (updated is null) return null;

        await _cache.InvalidateAsync(cancellationToken);
        await _events.OnUpdatedAsync(updated, cancellationToken);

        return updated;
    }

    /// <inheritdoc />
    /// <returns>The patched <see cref="Treatment"/>, or <see langword="null"/> if not found.</returns>
    public async Task<Treatment?> PatchTreatmentAsync(
        string id, JsonElement patchData, CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetByIdAsync(id, cancellationToken);
        if (existing is null) return null;

        // Apply patch fields to existing treatment
        ApplyJsonPatch(existing, patchData);

        // Re-decompose (idempotent upsert via LegacyId matching)
        await _decomposer.DecomposeAsync(existing, cancellationToken);

        await _cache.InvalidateAsync(cancellationToken);
        await _events.OnUpdatedAsync(existing, cancellationToken);

        return existing;
    }

    private static void ApplyJsonPatch(Treatment treatment, JsonElement patchData)
    {
        // JSON merge-patch: serialize existing, overlay patch properties, deserialize back
        var existingJson = JsonSerializer.Serialize(treatment);
        using var existingDoc = JsonDocument.Parse(existingJson);

        var merged = new Dictionary<string, object?>();
        foreach (var prop in existingDoc.RootElement.EnumerateObject())
            merged[prop.Name] = prop.Value.Clone();
        foreach (var prop in patchData.EnumerateObject())
            merged[prop.Name] = prop.Value.Clone();

        var mergedJson = JsonSerializer.Serialize(merged);
        var patched = JsonSerializer.Deserialize<Treatment>(mergedJson);
        if (patched == null) return;

        // Copy patched values back to the existing treatment in-place
        var props = typeof(Treatment).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        foreach (var prop in props)
        {
            if (!prop.CanWrite) continue;
            try
            {
                prop.SetValue(treatment, prop.GetValue(patched));
            }
            catch { /* skip computed properties that throw on set */ }
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteTreatmentAsync(
        string id, CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetByIdAsync(id, cancellationToken);
        var deleted = await _store.DeleteAsync(id, cancellationToken);

        if (deleted)
        {
            await _cache.InvalidateAsync(cancellationToken);
            if (existing is not null)
                await _events.OnDeletedAsync(existing, cancellationToken);
        }

        return deleted;
    }

    /// <inheritdoc />
    /// <returns>The number of treatments deleted.</returns>
    public async Task<long> DeleteTreatmentsAsync(
        string? find = null, CancellationToken cancellationToken = default)
    {
        var count = await _decomposer.BulkDeleteAsync(find, cancellationToken);
        if (count > 0)
            await _cache.InvalidateAsync(cancellationToken);
        return count;
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
        List<Treatment> treatments, CancellationToken cancellationToken)
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
