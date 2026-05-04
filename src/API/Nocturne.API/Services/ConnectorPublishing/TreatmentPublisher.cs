using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
/// Publishes treatment data received from connectors into both the legacy v1-v3 treatment store
/// (via <see cref="ITreatmentService"/>) and the v4 event-centric repositories for boluses, carb
/// intakes, BG checks, bolus calculations, and temporary basals.
/// </summary>
/// <seealso cref="ITreatmentPublisher"/>
internal sealed class TreatmentPublisher : ITreatmentPublisher
{
    private readonly NocturneDbContext _dbContext;
    private readonly ITreatmentService _treatmentService;
    private readonly IBolusRepository _bolusRepository;
    private readonly ICarbIntakeRepository _carbIntakeRepository;
    private readonly IBGCheckRepository _bgCheckRepository;
    private readonly IBolusCalculationRepository _bolusCalculationRepository;
    private readonly ITempBasalRepository _tempBasalRepository;
    private readonly ILogger<TreatmentPublisher> _logger;

    public TreatmentPublisher(
        NocturneDbContext dbContext,
        ITreatmentService treatmentService,
        IBolusRepository bolusRepository,
        ICarbIntakeRepository carbIntakeRepository,
        IBGCheckRepository bgCheckRepository,
        IBolusCalculationRepository bolusCalculationRepository,
        ITempBasalRepository tempBasalRepository,
        ILogger<TreatmentPublisher> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _treatmentService = treatmentService ?? throw new ArgumentNullException(nameof(treatmentService));
        _bolusRepository = bolusRepository ?? throw new ArgumentNullException(nameof(bolusRepository));
        _carbIntakeRepository = carbIntakeRepository ?? throw new ArgumentNullException(nameof(carbIntakeRepository));
        _bgCheckRepository = bgCheckRepository ?? throw new ArgumentNullException(nameof(bgCheckRepository));
        _bolusCalculationRepository = bolusCalculationRepository ?? throw new ArgumentNullException(nameof(bolusCalculationRepository));
        _tempBasalRepository = tempBasalRepository ?? throw new ArgumentNullException(nameof(tempBasalRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> PublishTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _treatmentService.CreateTreatmentsAsync(treatments, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish treatments for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishBolusesAsync(
        IEnumerable<Bolus> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            await _bolusRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug("Published {Count} Bolus records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish Bolus records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishCarbIntakesAsync(
        IEnumerable<CarbIntake> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            await _carbIntakeRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug("Published {Count} CarbIntake records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish CarbIntake records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishBGChecksAsync(
        IEnumerable<BGCheck> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            await _bgCheckRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug("Published {Count} BGCheck records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish BGCheck records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishBolusCalculationsAsync(
        IEnumerable<BolusCalculation> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            await _bolusCalculationRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug("Published {Count} BolusCalculation records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish BolusCalculation records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishTempBasalsAsync(
        IEnumerable<TempBasal> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            var minTimestamp = recordList.Min(r => r.StartTimestamp);
            var maxTimestamp = recordList.Max(r => r.StartTimestamp);

            await _tempBasalRepository.DeleteBySourceAndDateRangeAsync(
                source, minTimestamp, maxTimestamp, cancellationToken);

            await _tempBasalRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug("Published {Count} TempBasal records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish TempBasal records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishDecompositionBatchesAsync(
        IEnumerable<DecompositionBatch> batches,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var batchList = batches.ToList();
            if (batchList.Count == 0) return true;

            foreach (var batch in batchList)
            {
                _dbContext.DecompositionBatches.Add(new DecompositionBatchEntity
                {
                    Id = batch.Id,
                    TenantId = _dbContext.TenantId,
                    Source = batch.Source,
                    SourceRecordId = batch.SourceRecordId,
                    CreatedAt = batch.CreatedAt,
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Published {Count} DecompositionBatch records for {Source}", batchList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish DecompositionBatch records for {Source}", source);
            return false;
        }
    }

    public async Task<DateTime?> GetLatestTreatmentTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        // TODO: Filter by source to support multi-connector catch-up. Currently returns global latest.
        var latest = (await _treatmentService.GetTreatmentsAsync(
                count: 1,
                skip: 0,
                cancellationToken: cancellationToken))
            .FirstOrDefault();

        if (latest == null)
            return null;

        if (!string.IsNullOrEmpty(latest.CreatedAt)
            && DateTime.TryParse(latest.CreatedAt, out var createdAt))
            return createdAt;

        if (latest.Mills > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(latest.Mills).UtcDateTime;

        return null;
    }
}
