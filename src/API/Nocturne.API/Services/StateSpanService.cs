using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Domain service for StateSpan operations with temp basal translation
/// </summary>
public class StateSpanService : IStateSpanService
{
    private readonly StateSpanRepository _repository;
    private readonly ILogger<StateSpanService> _logger;

    public StateSpanService(
        StateSpanRepository repository,
        ILogger<StateSpanService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StateSpan>> GetStateSpansAsync(
        StateSpanCategory? category = null,
        string? state = null,
        long? from = null,
        long? to = null,
        string? source = null,
        bool? active = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Getting state spans with category: {Category}, state: {State}, from: {From}, to: {To}, source: {Source}, active: {Active}, count: {Count}, skip: {Skip}",
            category, state, from, to, source, active, count, skip);

        return await _repository.GetStateSpansAsync(
            category, state, from, to, source, active, count, skip, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StateSpan?> GetStateSpanByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting state span by ID: {Id}", id);

        return await _repository.GetStateSpanByIdAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StateSpan> UpsertStateSpanAsync(
        StateSpan stateSpan,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Upserting state span with OriginalId: {OriginalId}, Category: {Category}",
            stateSpan.OriginalId, stateSpan.Category);

        return await _repository.UpsertStateSpanAsync(stateSpan, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting state span with ID: {Id}", id);

        return await _repository.DeleteStateSpanAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StateSpan?> UpdateStateSpanAsync(
        string id,
        StateSpan stateSpan,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Updating state span with ID: {Id}, Category: {Category}",
            id, stateSpan.Category);

        return await _repository.UpdateStateSpanAsync(id, stateSpan, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetTempBasalsAsTreatmentsAsync(
        long? from = null,
        long? to = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Getting temp basals as treatments with from: {From}, to: {To}, count: {Count}, skip: {Skip}",
            from, to, count, skip);

        // Get TempBasal StateSpans from repository
        var stateSpans = await _repository.GetStateSpansAsync(
            category: StateSpanCategory.TempBasal,
            from: from,
            to: to,
            count: count,
            skip: skip,
            cancellationToken: cancellationToken);

        // Convert each StateSpan to a Treatment using the mapper
        var treatments = stateSpans
            .Select(TreatmentStateSpanMapper.ToTreatment)
            .Where(t => t != null)
            .Cast<Treatment>()
            .ToList();

        _logger.LogDebug("Converted {Count} temp basal state spans to treatments", treatments.Count);

        return treatments;
    }

    /// <inheritdoc />
    public async Task<StateSpan> CreateTempBasalFromTreatmentAsync(
        Treatment treatment,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Creating temp basal from treatment with ID: {Id}, EventType: {EventType}",
            treatment.Id, treatment.EventType);

        // Convert Treatment to StateSpan using the mapper
        var stateSpan = TreatmentStateSpanMapper.ToStateSpan(treatment);

        if (stateSpan == null)
        {
            _logger.LogWarning(
                "Treatment with ID {Id} is not a temp basal treatment (EventType: {EventType})",
                treatment.Id, treatment.EventType);

            throw new ArgumentException(
                $"Treatment with EventType '{treatment.EventType}' is not a valid temp basal treatment",
                nameof(treatment));
        }

        // Upsert the StateSpan
        var result = await _repository.UpsertStateSpanAsync(stateSpan, cancellationToken);

        _logger.LogDebug(
            "Created temp basal state span with ID: {Id} from treatment",
            result.Id);

        return result;
    }
}
