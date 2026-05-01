using System.Text.Json;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Entries;
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

        return await GetByLegacyIdAsync(id, ct);
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
                var result = await _decomposer.DecomposeAsync(treatment, ct);
                var tempBasal = result.CreatedRecords
                    .OfType<Core.Models.V4.TempBasal>()
                    .FirstOrDefault();
                if (tempBasal != null)
                    results.Add(TempBasalToTreatmentMapper.ToTreatment(tempBasal));
                else
                    results.Add(treatment);
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

        treatment.Id = id;
        try
        {
            await _decomposer.DecomposeAsync(treatment, ct);
            return await GetByIdAsync(id, ct);
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
        var deleted = await _pipeline.DeleteByLegacyIdAsync<Treatment>(id, ct);

        // Also check TempBasal (not covered by the pipeline's LegacyId delete)
        var tempBasal = await _tempBasalRepo.GetByLegacyIdAsync(id, ct);
        if (tempBasal == null && Guid.TryParse(id, out var guid))
            tempBasal = await _tempBasalRepo.GetByIdAsync(guid, ct);

        if (tempBasal != null)
        {
            try
            {
                await _tempBasalRepo.DeleteAsync(tempBasal.Id, ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete TempBasal record {Id}", tempBasal.Id);
                return false;
            }
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
