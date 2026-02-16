using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Service for backfilling existing legacy entries and treatments into v4 granular tables.
/// Reads from the legacy entries/treatments tables, converts to domain models,
/// and decomposes them into the appropriate v4 records.
/// The decomposers are idempotent via LegacyId, so re-running is safe.
/// </summary>
public class V4BackfillService
{
    private readonly IEntryDecomposer _entryDecomposer;
    private readonly ITreatmentDecomposer _treatmentDecomposer;
    private readonly NocturneDbContext _context;
    private readonly ILogger<V4BackfillService> _logger;

    private const int BatchSize = 1000;

    /// <summary>
    /// Event types that should be skipped during treatment backfill because they are
    /// already handled as StateSpans (temp basals and profile switches).
    /// </summary>
    private static readonly string[] SkippedEventTypes =
    [
        "Temp Basal",
        "Temp Basal Start",
        "TempBasal",
        "Profile Switch",
    ];

    public V4BackfillService(
        IEntryDecomposer entryDecomposer,
        ITreatmentDecomposer treatmentDecomposer,
        NocturneDbContext context,
        ILogger<V4BackfillService> logger
    )
    {
        _entryDecomposer = entryDecomposer;
        _treatmentDecomposer = treatmentDecomposer;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Backfills all existing entries and treatments into the v4 granular tables.
    /// Processes records in batches ordered by Mills ascending.
    /// </summary>
    public async Task<BackfillResult> BackfillAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("V4 backfill started");

        var result = new BackfillResult();

        await BackfillEntriesAsync(result, ct);
        await BackfillTreatmentsAsync(result, ct);

        _logger.LogInformation(
            "V4 backfill completed. Entries: {EntriesProcessed} processed, {EntriesFailed} failed. Treatments: {TreatmentsProcessed} processed, {TreatmentsFailed} failed, {TreatmentsSkipped} skipped",
            result.EntriesProcessed,
            result.EntriesFailed,
            result.TreatmentsProcessed,
            result.TreatmentsFailed,
            result.TreatmentsSkipped
        );

        return result;
    }

    private async Task BackfillEntriesAsync(BackfillResult result, CancellationToken ct)
    {
        var totalEntries = await _context.Entries.CountAsync(ct);
        _logger.LogInformation("Backfill entries: {Total} total entries to process", totalEntries);

        if (totalEntries == 0)
            return;

        long lastMills = long.MinValue;
        Guid lastId = Guid.Empty;
        var processed = 0;

        while (!ct.IsCancellationRequested)
        {
            // Use composite cursor (Mills, Id) to handle records with identical Mills values
            var batch = await _context
                .Entries.AsNoTracking()
                .Where(e =>
                    e.Mills > lastMills || (e.Mills == lastMills && e.Id.CompareTo(lastId) > 0)
                )
                .OrderBy(e => e.Mills)
                .ThenBy(e => e.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
                break;

            foreach (var entity in batch)
            {
                try
                {
                    var entry = EntryMapper.ToDomainModel(entity);
                    await _entryDecomposer.DecomposeAsync(entry, ct);
                    result.EntriesProcessed++;
                }
                catch (Exception ex)
                {
                    result.EntriesFailed++;
                    _logger.LogWarning(
                        ex,
                        "Failed to decompose entry {EntryId} (Mills={Mills})",
                        entity.Id,
                        entity.Mills
                    );
                }
            }

            lastMills = batch[^1].Mills;
            lastId = batch[^1].Id;
            processed += batch.Count;

            _logger.LogInformation(
                "Backfill entries: processed {Count}/{Total}",
                processed,
                totalEntries
            );

            if (batch.Count < BatchSize)
                break;
        }
    }

    private async Task BackfillTreatmentsAsync(BackfillResult result, CancellationToken ct)
    {
        var totalTreatments = await _context.Treatments.CountAsync(ct);
        _logger.LogInformation(
            "Backfill treatments: {Total} total treatments to process",
            totalTreatments
        );

        if (totalTreatments == 0)
            return;

        long lastMills = long.MinValue;
        Guid lastId = Guid.Empty;
        var processed = 0;

        while (!ct.IsCancellationRequested)
        {
            // Use composite cursor (Mills, Id) to handle records with identical Mills values
            var batch = await _context
                .Treatments.AsNoTracking()
                .Where(t =>
                    t.Mills > lastMills || (t.Mills == lastMills && t.Id.CompareTo(lastId) > 0)
                )
                .OrderBy(t => t.Mills)
                .ThenBy(t => t.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
                break;

            foreach (var entity in batch)
            {
                try
                {
                    var treatment = TreatmentMapper.ToDomainModel(entity);

                    // Skip TempBasal and ProfileSwitch treatments — these are already
                    // handled as StateSpans by the decomposer for new writes
                    if (ShouldSkipTreatment(treatment))
                    {
                        result.TreatmentsSkipped++;
                        continue;
                    }

                    await _treatmentDecomposer.DecomposeAsync(treatment, ct);
                    result.TreatmentsProcessed++;
                }
                catch (Exception ex)
                {
                    result.TreatmentsFailed++;
                    _logger.LogWarning(
                        ex,
                        "Failed to decompose treatment {TreatmentId} (Mills={Mills}, EventType={EventType})",
                        entity.Id,
                        entity.Mills,
                        entity.EventType
                    );
                }
            }

            lastMills = batch[^1].Mills;
            lastId = batch[^1].Id;
            processed += batch.Count;

            _logger.LogInformation(
                "Backfill treatments: processed {Count}/{Total}",
                processed,
                totalTreatments
            );

            if (batch.Count < BatchSize)
                break;
        }
    }

    /// <summary>
    /// Determines if a treatment should be skipped during backfill.
    /// Temp basals and profile switches are already represented as StateSpans.
    /// </summary>
    private static bool ShouldSkipTreatment(Core.Models.Treatment treatment)
    {
        if (string.IsNullOrEmpty(treatment.EventType))
            return false;

        return SkippedEventTypes.Any(eventType =>
            string.Equals(treatment.EventType, eventType, StringComparison.OrdinalIgnoreCase)
        );
    }
}

/// <summary>
/// Result of a V4 backfill operation
/// </summary>
public class BackfillResult
{
    /// <summary>
    /// Number of entries successfully decomposed into v4 records
    /// </summary>
    public long EntriesProcessed { get; set; }

    /// <summary>
    /// Number of entries that failed decomposition
    /// </summary>
    public long EntriesFailed { get; set; }

    /// <summary>
    /// Number of treatments successfully decomposed into v4 records
    /// </summary>
    public long TreatmentsProcessed { get; set; }

    /// <summary>
    /// Number of treatments that failed decomposition
    /// </summary>
    public long TreatmentsFailed { get; set; }

    /// <summary>
    /// Number of treatments skipped (temp basals, profile switches)
    /// </summary>
    public long TreatmentsSkipped { get; set; }
}
