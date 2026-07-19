using System.Text.Json;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Entries;
using Nocturne.Core.Models.V4;
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
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Treatment>> QueryAsync(TreatmentQuery query, CancellationToken ct = default)
    {
        var (fromMills, toMills) = ParseTimeRangeFromFind(query.Find);

        var projected = await _projection.GetProjectedTreatmentsAsync(
            fromMills, toMills, query.Count + query.Skip, nativeOnly: false, ct: ct);

        var results = projected
            .OrderByDescending(t => t.Mills)
            .Skip(query.Skip)
            .Take(query.Count)
            .ToList();

        if (query.ReverseResults)
            return results.OrderBy(t => t.Mills).ToList();

        return results;
    }

    /// <inheritdoc />
    public async Task<Treatment?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (Guid.TryParse(id, out var guid))
            return await GetByGuidAsync(guid, ct);

        // A non-UUID id is either a legacy/AAPS-supplied ObjectId (stored as LegacyId) or a 24-hex
        // ObjectId we derived from the record's UUID for the wire. Try the exact LegacyId match
        // first, then resolve a derived ObjectId via its uuid prefix range.
        var byLegacy = await GetByLegacyIdAsync(id, ct);
        if (byLegacy != null)
            return byLegacy;

        if (MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
            return await ResolveByGuidRangeAsync(low, high, ct);

        return null;
    }

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
    public async Task<IReadOnlyList<Treatment>> CreateAsync(
        IReadOnlyList<Treatment> treatments, CancellationToken ct = default)
    {
        var results = new List<Treatment>();

        foreach (var treatment in treatments)
        {
            try
            {
                var result = await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live, ct);
                var tempBasal = result.CreatedRecords
                    .OfType<Core.Models.V4.TempBasal>()
                    .FirstOrDefault();
                if (tempBasal != null)
                    results.Add(TempBasalToTreatmentMapper.ToTreatment(tempBasal));
                else
                    results.Add(treatment);
            }
            catch (OperationCanceledException)
            {
                // A canceled request (client disconnect, shutdown) is control flow,
                // not a bad treatment. Let it abort the batch — swallowing it here
                // turns one cancellation into a per-record "failure" logged for every
                // remaining treatment, since each subsequent DB call on the canceled
                // token throws too.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decompose treatment {Id}", treatment.Id);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<Treatment?> UpdateAsync(string id, Treatment treatment, CancellationToken ct = default)
    {
        var existing = await GetByIdAsync(id, ct);
        if (existing == null) return null;

        // Re-key to the stored LegacyId so the decomposer upserts the existing record in place
        // rather than creating a duplicate when the client sends a derived ObjectId.
        treatment.Id = await ResolveCanonicalIdAsync(id, ct) ?? id;
        try
        {
            await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live, ct);
            return await GetByIdAsync(id, ct);
        }
        catch (OperationCanceledException)
        {
            // Canceled request is control flow, not an update failure — propagate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update treatment {Id}", id);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var deleted = await _pipeline.DeleteByLegacyIdAsync<Treatment>(id, WriteOrigin.Live, ct);

        // Also check TempBasal (not covered by the pipeline's LegacyId delete)
        var tempBasal = await _tempBasalRepo.GetByLegacyIdAsync(id, ct);
        if (tempBasal == null && Guid.TryParse(id, out var guid))
            tempBasal = await _tempBasalRepo.GetByIdAsync(guid, ct);

        if (tempBasal != null)
        {
            try
            {
                await _tempBasalRepo.DeleteAsync(tempBasal.Id, WriteOrigin.Live, ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete TempBasal record {Id}", tempBasal.Id);
                return false;
            }
        }

        // A 24-hex ObjectId AAPS derived from the record's UUID: resolve it via the uuid prefix
        // range. Prefer deleting by the resolved record's LegacyId so correlated siblings (e.g. a
        // meal bolus's carb, a bolus wizard's calculation) are removed together — DeleteByGuidRange
        // only deletes the single matched row, which would orphan the sibling into a phantom.
        if (deleted == 0 && MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
        {
            var legacyId = await FindLegacyIdByGuidRangeAsync(low, high, ct);
            if (!string.IsNullOrEmpty(legacyId))
            {
                deleted = await _pipeline.DeleteByLegacyIdAsync<Treatment>(legacyId, WriteOrigin.Live, ct);
                if (deleted > 0)
                    return true;
            }

            // Native row with no LegacyId (no correlated sibling to worry about): delete by UUID.
            if (await DeleteByGuidRangeAsync(low, high, ct))
                return true;
        }

        return deleted > 0;
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(string? find = null, CancellationToken ct = default)
    {
        var (fromMills, toMills) = ParseTimeRangeFromFind(find);
        var from = fromMills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills.Value).UtcDateTime : (DateTime?)null;
        var to = toMills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(toMills.Value).UtcDateTime : (DateTime?)null;

        var bolusCount = await _bolusRepo.CountAsync(from, to, ct);
        var carbCount = await _carbIntakeRepo.CountAsync(from, to, ct);
        var bgCheckCount = await _bgCheckRepo.CountAsync(from, to, ct);
        var noteCount = await _noteRepo.CountAsync(from, to, ct);
        var deviceEventCount = await _deviceEventRepo.CountAsync(from, to, ct);
        var tempBasalCount = await _tempBasalRepo.CountAsync(from, to, ct);
        var bolusCalcCount = await _bolusCalcRepo.CountAsync(from, to, ct);

        return bolusCount + carbCount + bgCheckCount + noteCount
             + deviceEventCount + tempBasalCount + bolusCalcCount;
    }

    #region Private — GetById helpers

    private async Task<Treatment?> GetByGuidAsync(Guid id, CancellationToken ct)
    {
        var idStr = id.ToString();

        // Search across all V4 repos by ID, project at that timestamp with a
        // reasonable limit, and find the projected treatment that contains this ID.
        var bolus = await _bolusRepo.GetByIdAsync(id, ct);
        if (bolus != null)
            return await FindProjectedTreatmentAsync(bolus.Mills, idStr, ct);

        var carbIntake = await _carbIntakeRepo.GetByIdAsync(id, ct);
        if (carbIntake != null)
        {
            // CarbIntake paired into a Meal Bolus gets the Bolus's ID as the projected Treatment.Id.
            if (carbIntake.CorrelationId.HasValue)
            {
                var pairedBoluses = await _bolusRepo.GetByCorrelationIdAsync(carbIntake.CorrelationId.Value, ct);
                var pairedBolus = pairedBoluses.FirstOrDefault();
                if (pairedBolus != null)
                    return await FindProjectedTreatmentAsync(pairedBolus.Mills, pairedBolus.Id.ToString(), ct);
            }
            // Unpaired carb correction: the projected Treatment.Id is the CarbIntake's ID
            return await FindProjectedTreatmentAsync(carbIntake.Mills, idStr, ct);
        }

        var bgCheck = await _bgCheckRepo.GetByIdAsync(id, ct);
        if (bgCheck != null)
            return await FindProjectedTreatmentAsync(bgCheck.Mills, idStr, ct);

        var note = await _noteRepo.GetByIdAsync(id, ct);
        if (note != null)
            return await FindProjectedTreatmentAsync(note.Mills, idStr, ct);

        var deviceEvent = await _deviceEventRepo.GetByIdAsync(id, ct);
        if (deviceEvent != null)
            return await FindProjectedTreatmentAsync(deviceEvent.Mills, idStr, ct);

        var bolusCalc = await _bolusCalcRepo.GetByIdAsync(id, ct);
        if (bolusCalc != null)
            return await FindProjectedTreatmentAsync(bolusCalc.Mills, idStr, ct);

        var tempBasal = await _tempBasalRepo.GetByIdAsync(id, ct);
        if (tempBasal != null)
            return TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        return null;
    }

    /// <summary>
    /// Resolves a UUID prefix range (from a derived 24-hex ObjectId) to a projected treatment by
    /// finding which V4 table holds the record, then reusing the by-UUID projection logic so meal
    /// pairing and temp-basal mapping are handled identically to a normal lookup.
    /// </summary>
    private async Task<Treatment?> ResolveByGuidRangeAsync(Guid low, Guid high, CancellationToken ct)
    {
        var bolus = await _bolusRepo.GetByGuidRangeAsync(low, high, ct);
        if (bolus != null)
            return await GetByGuidAsync(bolus.Id, ct);

        var carbIntake = await _carbIntakeRepo.GetByGuidRangeAsync(low, high, ct);
        if (carbIntake != null)
            return await GetByGuidAsync(carbIntake.Id, ct);

        var bgCheck = await _bgCheckRepo.GetByGuidRangeAsync(low, high, ct);
        if (bgCheck != null)
            return await GetByGuidAsync(bgCheck.Id, ct);

        var note = await _noteRepo.GetByGuidRangeAsync(low, high, ct);
        if (note != null)
            return await GetByGuidAsync(note.Id, ct);

        var deviceEvent = await _deviceEventRepo.GetByGuidRangeAsync(low, high, ct);
        if (deviceEvent != null)
            return await GetByGuidAsync(deviceEvent.Id, ct);

        var bolusCalc = await _bolusCalcRepo.GetByGuidRangeAsync(low, high, ct);
        if (bolusCalc != null)
            return await GetByGuidAsync(bolusCalc.Id, ct);

        var tempBasal = await _tempBasalRepo.GetByGuidRangeAsync(low, high, ct);
        if (tempBasal != null)
            return TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        return null;
    }

    /// <summary>
    /// Maps a wire id (a 24-hex ObjectId derived from a record's UUID) to the <c>LegacyId</c> the
    /// decomposer upserts on, so an update re-decomposes the existing record in place instead of
    /// creating a duplicate. Returns null for a raw UUID or an id that is already the stored key
    /// (the caller falls back to the existing id in that case).
    /// </summary>
    public async Task<string?> ResolveCanonicalIdAsync(string id, CancellationToken ct = default)
    {
        if (Guid.TryParse(id, out _))
            return null;

        if (!MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
            return null;

        // Return the decomposer's upsert key (LegacyId). A native V4 row has no LegacyId, so the
        // decomposer can't match it and would insert a duplicate; backfill it with the derived
        // ObjectId (which is what the wire already shows for this record) so the update lands in
        // place. Short-circuits on the first repo that owns the record.
        return await ResolveOrBackfillLegacyIdAsync(_bolusRepo, low, high, ct)
            ?? await ResolveOrBackfillLegacyIdAsync(_carbIntakeRepo, low, high, ct)
            ?? await ResolveOrBackfillLegacyIdAsync(_bgCheckRepo, low, high, ct)
            ?? await ResolveOrBackfillLegacyIdAsync(_noteRepo, low, high, ct)
            ?? await ResolveOrBackfillLegacyIdAsync(_deviceEventRepo, low, high, ct)
            ?? await ResolveOrBackfillLegacyIdAsync(_bolusCalcRepo, low, high, ct)
            ?? await ResolveOrBackfillTempBasalLegacyIdAsync(low, high, ct);
    }

    private static async Task<string?> ResolveOrBackfillLegacyIdAsync<T>(
        IV4Repository<T> repo, Guid low, Guid high, CancellationToken ct) where T : class, IV4Record
    {
        var entity = await repo.GetByGuidRangeAsync(low, high, ct);
        if (entity is null) return null;
        if (!string.IsNullOrEmpty(entity.LegacyId)) return entity.LegacyId;

        var objectId = MongoObjectId.FromGuid(entity.Id);
        entity.LegacyId = objectId;
        await repo.UpdateAsync(entity.Id, entity, WriteOrigin.Live, ct);
        return objectId;
    }

    private async Task<string?> ResolveOrBackfillTempBasalLegacyIdAsync(Guid low, Guid high, CancellationToken ct)
    {
        var tempBasal = await _tempBasalRepo.GetByGuidRangeAsync(low, high, ct);
        if (tempBasal is null) return null;
        if (!string.IsNullOrEmpty(tempBasal.LegacyId)) return tempBasal.LegacyId;

        var objectId = MongoObjectId.FromGuid(tempBasal.Id);
        tempBasal.LegacyId = objectId;
        await _tempBasalRepo.UpdateAsync(tempBasal.Id, tempBasal, WriteOrigin.Live, ct);
        return objectId;
    }

    private async Task<string?> FindLegacyIdByGuidRangeAsync(Guid low, Guid high, CancellationToken ct)
    {
        var bolus = await _bolusRepo.GetByGuidRangeAsync(low, high, ct);
        if (bolus != null) return bolus.LegacyId;

        var carbIntake = await _carbIntakeRepo.GetByGuidRangeAsync(low, high, ct);
        if (carbIntake != null) return carbIntake.LegacyId;

        var bgCheck = await _bgCheckRepo.GetByGuidRangeAsync(low, high, ct);
        if (bgCheck != null) return bgCheck.LegacyId;

        var note = await _noteRepo.GetByGuidRangeAsync(low, high, ct);
        if (note != null) return note.LegacyId;

        var deviceEvent = await _deviceEventRepo.GetByGuidRangeAsync(low, high, ct);
        if (deviceEvent != null) return deviceEvent.LegacyId;

        var bolusCalc = await _bolusCalcRepo.GetByGuidRangeAsync(low, high, ct);
        if (bolusCalc != null) return bolusCalc.LegacyId;

        var tempBasal = await _tempBasalRepo.GetByGuidRangeAsync(low, high, ct);
        if (tempBasal != null) return tempBasal.LegacyId;

        return null;
    }

    /// <summary>Deletes the record inside a UUID prefix range (derived ObjectId) by its real UUID.</summary>
    private async Task<bool> DeleteByGuidRangeAsync(Guid low, Guid high, CancellationToken ct)
    {
        var bolus = await _bolusRepo.GetByGuidRangeAsync(low, high, ct);
        if (bolus != null) { await _bolusRepo.DeleteAsync(bolus.Id, WriteOrigin.Live, ct); return true; }

        var carbIntake = await _carbIntakeRepo.GetByGuidRangeAsync(low, high, ct);
        if (carbIntake != null) { await _carbIntakeRepo.DeleteAsync(carbIntake.Id, WriteOrigin.Live, ct); return true; }

        var bgCheck = await _bgCheckRepo.GetByGuidRangeAsync(low, high, ct);
        if (bgCheck != null) { await _bgCheckRepo.DeleteAsync(bgCheck.Id, WriteOrigin.Live, ct); return true; }

        var note = await _noteRepo.GetByGuidRangeAsync(low, high, ct);
        if (note != null) { await _noteRepo.DeleteAsync(note.Id, WriteOrigin.Live, ct); return true; }

        var deviceEvent = await _deviceEventRepo.GetByGuidRangeAsync(low, high, ct);
        if (deviceEvent != null) { await _deviceEventRepo.DeleteAsync(deviceEvent.Id, WriteOrigin.Live, ct); return true; }

        var bolusCalc = await _bolusCalcRepo.GetByGuidRangeAsync(low, high, ct);
        if (bolusCalc != null) { await _bolusCalcRepo.DeleteAsync(bolusCalc.Id, WriteOrigin.Live, ct); return true; }

        var tempBasal = await _tempBasalRepo.GetByGuidRangeAsync(low, high, ct);
        if (tempBasal != null) { await _tempBasalRepo.DeleteAsync(tempBasal.Id, WriteOrigin.Live, ct); return true; }

        return false;
    }

    private async Task<Treatment?> GetByLegacyIdAsync(string legacyId, CancellationToken ct)
    {
        var bolus = await _bolusRepo.GetByLegacyIdAsync(legacyId, ct);
        if (bolus != null)
            return await FindProjectedTreatmentAsync(bolus.Mills, bolus.Id.ToString(), ct);

        var carbIntake = await _carbIntakeRepo.GetByLegacyIdAsync(legacyId, ct);
        if (carbIntake != null)
        {
            if (carbIntake.CorrelationId.HasValue)
            {
                var pairedBoluses = await _bolusRepo.GetByCorrelationIdAsync(carbIntake.CorrelationId.Value, ct);
                var pairedBolus = pairedBoluses.FirstOrDefault();
                if (pairedBolus != null)
                    return await FindProjectedTreatmentAsync(pairedBolus.Mills, pairedBolus.Id.ToString(), ct);
            }
            return await FindProjectedTreatmentAsync(carbIntake.Mills, carbIntake.Id.ToString(), ct);
        }

        var bgCheck = await _bgCheckRepo.GetByLegacyIdAsync(legacyId, ct);
        if (bgCheck != null)
            return await FindProjectedTreatmentAsync(bgCheck.Mills, bgCheck.Id.ToString(), ct);

        var noteRecord = await _noteRepo.GetByLegacyIdAsync(legacyId, ct);
        if (noteRecord != null)
            return await FindProjectedTreatmentAsync(noteRecord.Mills, noteRecord.Id.ToString(), ct);

        var deviceEvent = await _deviceEventRepo.GetByLegacyIdAsync(legacyId, ct);
        if (deviceEvent != null)
            return await FindProjectedTreatmentAsync(deviceEvent.Mills, deviceEvent.Id.ToString(), ct);

        var bolusCalc = await _bolusCalcRepo.GetByLegacyIdAsync(legacyId, ct);
        if (bolusCalc != null)
            return await FindProjectedTreatmentAsync(bolusCalc.Mills, bolusCalc.Id.ToString(), ct);

        var tempBasal = await _tempBasalRepo.GetByLegacyIdAsync(legacyId, ct);
        if (tempBasal != null)
            return TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        return null;
    }

    private async Task<Treatment?> FindProjectedTreatmentAsync(
        long mills, string treatmentId, CancellationToken ct)
    {
        var projected = await _projection.GetProjectedTreatmentsAsync(
            mills, mills, 100, nativeOnly: false, ct: ct);
        return projected.FirstOrDefault(t => t.Id == treatmentId);
    }

    #endregion

    #region Private — Find query parsing

    private static (long? From, long? To) ParseTimeRangeFromFind(string? find)
        => EntryDomainLogic.ParseTimeRangeFromFind(find);

    #endregion
}
