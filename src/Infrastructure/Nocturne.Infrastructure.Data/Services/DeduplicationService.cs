using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Service for deduplicating records from multiple data sources.
/// Links records that represent the same underlying event and provides unified views.
/// </summary>
public class DeduplicationService : IDeduplicationService
{
    private readonly NocturneDbContext _context;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeduplicationService> _logger;

    private static readonly TimeSpan MatchingWindow = TimeSpan.FromSeconds(30);
    private static readonly long MatchingWindowMillis = (long)MatchingWindow.TotalMilliseconds;

    private static readonly ConcurrentDictionary<Guid, DeduplicationJobStatus> _runningJobs = new();
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellations = new();

    /// <summary>
    /// Event types that should be grouped together for deduplication.
    /// When a Basal and Temp Basal occur at the same time, they represent
    /// the same underlying event and should be deduplicated together.
    /// </summary>
    private static readonly HashSet<string> BasalRelatedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Basal",
        "Temp Basal"
    };

    /// <summary>
    /// Priority order for basal-related types. Higher priority types
    /// are preferred when merging duplicates.
    /// </summary>
    private static readonly Dictionary<string, int> BasalTypePriority = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Temp Basal", 1 },  // Highest priority - most specific
        { "Basal", 0 }       // Lower priority - generic
    };

    /// <inheritdoc cref="IDeduplicationService" />
    public DeduplicationService(
        NocturneDbContext context,
        IServiceScopeFactory scopeFactory,
        ILogger<DeduplicationService> logger)
    {
        _context = context;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Guid> GetOrCreateCanonicalIdAsync(
        RecordType recordType,
        long mills,
        MatchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        var recordTypeStr = recordType.ToString().ToLowerInvariant();
        var windowStart = mills - MatchingWindowMillis;
        var windowEnd = mills + MatchingWindowMillis;

        // Look for existing linked records in the time window
        var potentialMatches = await _context.LinkedRecords
            .Where(lr => lr.RecordType == recordTypeStr)
            .Where(lr => lr.SourceTimestamp >= windowStart && lr.SourceTimestamp <= windowEnd)
            .ToListAsync(cancellationToken);

        if (potentialMatches.Count == 0)
        {
            // No matches found, create a new canonical ID
            return Guid.CreateVersion7();
        }

        // For entries, check glucose value matching
        if (recordType == RecordType.Entry && criteria.GlucoseValue.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var entries = await _context.Entries
                    .Where(e => recordIds.Contains(e.Id))
                    .ToListAsync(cancellationToken);

                foreach (var entry in entries)
                {
                    var entryGlucose = entry.Sgv ?? entry.Mgdl;
                    if (Math.Abs(entryGlucose - criteria.GlucoseValue.Value) <= criteria.GlucoseTolerance)
                    {
                        // Match found based on glucose value
                        return canonicalId;
                    }
                }
            }
        }
        // For treatments, check event type and relevant values
        else if (recordType == RecordType.Treatment && !string.IsNullOrEmpty(criteria.EventType))
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var treatments = await _context.Treatments
                    .Where(t => recordIds.Contains(t.Id))
                    .ToListAsync(cancellationToken);

                foreach (var treatment in treatments)
                {
                    if (!string.Equals(treatment.EventType, criteria.EventType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Check insulin if relevant
                    if (criteria.Insulin.HasValue && treatment.Insulin.HasValue)
                    {
                        if (Math.Abs(treatment.Insulin.Value - criteria.Insulin.Value) > criteria.InsulinTolerance)
                            continue;
                    }

                    // Check carbs if relevant
                    if (criteria.Carbs.HasValue && treatment.Carbs.HasValue)
                    {
                        if (Math.Abs(treatment.Carbs.Value - criteria.Carbs.Value) > criteria.CarbsTolerance)
                            continue;
                    }

                    // Match found
                    return canonicalId;
                }
            }
        }
        // For state spans, check category and state
        else if (recordType == RecordType.StateSpan && criteria.Category.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();
            var categoryStr = criteria.Category.Value.ToString();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var stateSpans = await _context.StateSpans
                    .Where(s => recordIds.Contains(s.Id))
                    .ToListAsync(cancellationToken);

                foreach (var stateSpan in stateSpans)
                {
                    if (!string.Equals(stateSpan.Category, categoryStr, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrEmpty(criteria.State) &&
                        !string.Equals(stateSpan.State, criteria.State, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Match found
                    return canonicalId;
                }
            }
        }

        // No matching records found, create a new canonical ID
        return Guid.CreateVersion7();
    }

    /// <inheritdoc />
    public async Task LinkRecordAsync(
        Guid canonicalId,
        RecordType recordType,
        Guid recordId,
        long mills,
        string dataSource,
        CancellationToken cancellationToken = default)
    {
        var recordTypeStr = recordType.ToString().ToLowerInvariant();

        // Check if this record is already linked
        var existing = await _context.LinkedRecords
            .FirstOrDefaultAsync(lr =>
                lr.RecordType == recordTypeStr && lr.RecordId == recordId,
                cancellationToken);

        if (existing != null)
        {
            _logger.LogDebug(
                "Record {RecordType} {RecordId} already linked to canonical {CanonicalId}",
                recordType, recordId, existing.CanonicalId);
            return;
        }

        // Check if this should be the primary record (earliest timestamp)
        var existingInGroup = await _context.LinkedRecords
            .Where(lr => lr.CanonicalId == canonicalId)
            .OrderBy(lr => lr.SourceTimestamp)
            .FirstOrDefaultAsync(cancellationToken);

        var isPrimary = existingInGroup == null || mills < existingInGroup.SourceTimestamp;

        // If this is the new primary, demote the old primary
        if (isPrimary && existingInGroup != null)
        {
            existingInGroup.IsPrimary = false;
        }

        var linkedRecord = new LinkedRecordEntity
        {
            CanonicalId = canonicalId,
            RecordType = recordTypeStr,
            RecordId = recordId,
            SourceTimestamp = mills,
            DataSource = dataSource,
            IsPrimary = isPrimary
        };

        _context.LinkedRecords.Add(linkedRecord);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(
            "Linked {RecordType} {RecordId} to canonical {CanonicalId} (primary: {IsPrimary})",
            recordType, recordId, canonicalId, isPrimary);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<LinkedRecord>> GetLinkedRecordsAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.LinkedRecords
            .Where(lr => lr.CanonicalId == canonicalId)
            .OrderBy(lr => lr.SourceTimestamp)
            .ToListAsync(cancellationToken);

        return entities.Select(e => new LinkedRecord
        {
            Id = e.Id.ToString(),
            CanonicalId = e.CanonicalId,
            RecordType = Enum.Parse<RecordType>(e.RecordType, ignoreCase: true),
            RecordId = e.RecordId,
            SourceTimestamp = e.SourceTimestamp,
            DataSource = e.DataSource,
            IsPrimary = e.IsPrimary,
            CreatedAt = e.SysCreatedAt
        });
    }

    /// <inheritdoc />
    public async Task<LinkedRecord?> GetLinkedRecordAsync(
        RecordType recordType,
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        var recordTypeStr = recordType.ToString().ToLowerInvariant();

        var entity = await _context.LinkedRecords
            .FirstOrDefaultAsync(lr =>
                lr.RecordType == recordTypeStr && lr.RecordId == recordId,
                cancellationToken);

        if (entity == null)
            return null;

        return new LinkedRecord
        {
            Id = entity.Id.ToString(),
            CanonicalId = entity.CanonicalId,
            RecordType = recordType,
            RecordId = entity.RecordId,
            SourceTimestamp = entity.SourceTimestamp,
            DataSource = entity.DataSource,
            IsPrimary = entity.IsPrimary,
            CreatedAt = entity.SysCreatedAt
        };
    }

    /// <inheritdoc />
    public async Task<Entry?> GetUnifiedEntryAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default)
    {
        var linkedRecords = await _context.LinkedRecords
            .Where(lr => lr.CanonicalId == canonicalId && lr.RecordType == "entry")
            .OrderBy(lr => lr.SourceTimestamp)
            .ToListAsync(cancellationToken);

        if (linkedRecords.Count == 0)
            return null;

        var recordIds = linkedRecords.Select(lr => lr.RecordId).ToList();
        var entries = await _context.Entries
            .Where(e => recordIds.Contains(e.Id))
            .ToListAsync(cancellationToken);

        if (entries.Count == 0)
            return null;

        // Sort by timestamp to get primary first
        var sortedEntries = entries
            .OrderBy(e => e.Mills)
            .Select(EntryMapper.ToDomainModel)
            .ToList();

        return MergeEntries(sortedEntries, canonicalId);
    }

    /// <inheritdoc />
    public async Task<Treatment?> GetUnifiedTreatmentAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default)
    {
        var linkedRecords = await _context.LinkedRecords
            .Where(lr => lr.CanonicalId == canonicalId && lr.RecordType == "treatment")
            .OrderBy(lr => lr.SourceTimestamp)
            .ToListAsync(cancellationToken);

        if (linkedRecords.Count == 0)
            return null;

        var recordIds = linkedRecords.Select(lr => lr.RecordId).ToList();
        var treatments = await _context.Treatments
            .Where(t => recordIds.Contains(t.Id))
            .ToListAsync(cancellationToken);

        if (treatments.Count == 0)
            return null;

        // Sort by timestamp to get primary first
        var sortedTreatments = treatments
            .OrderBy(t => t.Mills)
            .Select(TreatmentMapper.ToDomainModel)
            .ToList();

        return MergeTreatments(sortedTreatments, canonicalId);
    }

    /// <inheritdoc />
    public async Task<StateSpan?> GetUnifiedStateSpanAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default)
    {
        var linkedRecords = await _context.LinkedRecords
            .Where(lr => lr.CanonicalId == canonicalId && lr.RecordType == "statespan")
            .OrderBy(lr => lr.SourceTimestamp)
            .ToListAsync(cancellationToken);

        if (linkedRecords.Count == 0)
            return null;

        var recordIds = linkedRecords.Select(lr => lr.RecordId).ToList();
        var stateSpans = await _context.StateSpans
            .Where(s => recordIds.Contains(s.Id))
            .ToListAsync(cancellationToken);

        if (stateSpans.Count == 0)
            return null;

        // Sort by timestamp to get primary first
        var sortedStateSpans = stateSpans
            .OrderBy(s => s.StartMills)
            .Select(StateSpanMapper.ToDomainModel)
            .ToList();

        return MergeStateSpans(sortedStateSpans, canonicalId);
    }

    /// <inheritdoc />
    public async Task<DeduplicationResult> DeduplicateAllAsync(
        IProgress<DeduplicationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var entryCount = await _context.Entries.CountAsync(cancellationToken);
            var treatmentCount = await _context.Treatments.CountAsync(cancellationToken);
            var stateSpanCount = await _context.StateSpans.CountAsync(cancellationToken);
            var totalRecords = entryCount + treatmentCount + stateSpanCount;

            var processed = 0;
            var groupsCreated = 0;
            var recordsLinked = 0;
            var duplicateGroups = 0;

            // Process entries
            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = processed,
                GroupsFound = groupsCreated,
                RecordsLinked = recordsLinked,
                CurrentPhase = "Entries"
            });

            var entryResult = await DeduplicateEntriesAsync(progress, totalRecords, processed, cancellationToken);
            processed += entryResult.processed;
            groupsCreated += entryResult.groups;
            recordsLinked += entryResult.linked;
            duplicateGroups += entryResult.duplicates;

            // Process treatments
            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = processed,
                GroupsFound = groupsCreated,
                RecordsLinked = recordsLinked,
                CurrentPhase = "Treatments"
            });

            var treatmentResult = await DeduplicateTreatmentsAsync(progress, totalRecords, processed, cancellationToken);
            processed += treatmentResult.processed;
            groupsCreated += treatmentResult.groups;
            recordsLinked += treatmentResult.linked;
            duplicateGroups += treatmentResult.duplicates;

            // Process state spans
            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = processed,
                GroupsFound = groupsCreated,
                RecordsLinked = recordsLinked,
                CurrentPhase = "StateSpans"
            });

            var stateSpanResult = await DeduplicateStateSpansAsync(progress, totalRecords, processed, cancellationToken);
            processed += stateSpanResult.processed;
            groupsCreated += stateSpanResult.groups;
            recordsLinked += stateSpanResult.linked;
            duplicateGroups += stateSpanResult.duplicates;

            stopwatch.Stop();

            _logger.LogInformation(
                "Deduplication completed: {TotalRecords} records processed, {Groups} groups created, {Linked} records linked, {Duplicates} duplicate groups in {Duration}",
                processed, groupsCreated, recordsLinked, duplicateGroups, stopwatch.Elapsed);

            return new DeduplicationResult
            {
                TotalRecordsProcessed = processed,
                CanonicalGroupsCreated = groupsCreated,
                RecordsLinked = recordsLinked,
                DuplicateGroupsFound = duplicateGroups,
                Duration = stopwatch.Elapsed,
                EntriesProcessed = entryResult.processed,
                TreatmentsProcessed = treatmentResult.processed,
                StateSpansProcessed = stateSpanResult.processed,
                Success = true
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Deduplication cancelled after {Duration}", stopwatch.Elapsed);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deduplication failed after {Duration}", stopwatch.Elapsed);
            return new DeduplicationResult
            {
                Duration = stopwatch.Elapsed,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<Guid> StartDeduplicationJobAsync(CancellationToken cancellationToken = default)
    {
        var jobId = Guid.CreateVersion7();
        var cts = new CancellationTokenSource();

        var status = new DeduplicationJobStatus
        {
            JobId = jobId,
            State = DeduplicationJobState.Pending,
            StartedAt = DateTime.UtcNow
        };

        _runningJobs[jobId] = status;
        _jobCancellations[jobId] = cts;

        // Start the job in the background with its own scope
        _ = Task.Run(async () =>
        {
            // Create a new scope for the background work to get a fresh DbContext
            await using var scope = _scopeFactory.CreateAsyncScope();
            var scopedService = scope.ServiceProvider.GetRequiredService<IDeduplicationService>();

            try
            {
                _runningJobs[jobId] = status with { State = DeduplicationJobState.Running };

                var progressReporter = new Progress<DeduplicationProgress>(p =>
                {
                    if (_runningJobs.TryGetValue(jobId, out var currentStatus))
                    {
                        _runningJobs[jobId] = currentStatus with { Progress = p };
                    }
                });

                var result = await scopedService.DeduplicateAllAsync(progressReporter, cts.Token);

                _runningJobs[jobId] = _runningJobs[jobId] with
                {
                    State = result.Success ? DeduplicationJobState.Completed : DeduplicationJobState.Failed,
                    CompletedAt = DateTime.UtcNow,
                    Result = result
                };
            }
            catch (OperationCanceledException)
            {
                _runningJobs[jobId] = _runningJobs[jobId] with
                {
                    State = DeduplicationJobState.Cancelled,
                    CompletedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deduplication job {JobId} failed", jobId);
                _runningJobs[jobId] = _runningJobs[jobId] with
                {
                    State = DeduplicationJobState.Failed,
                    CompletedAt = DateTime.UtcNow,
                    Result = new DeduplicationResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    }
                };
            }
            finally
            {
                _jobCancellations.TryRemove(jobId, out _);
            }
        });

        return jobId;
    }

    /// <inheritdoc />
    public Task<DeduplicationJobStatus?> GetJobStatusAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        _runningJobs.TryGetValue(jobId, out var status);
        return Task.FromResult(status);
    }

    /// <inheritdoc />
    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (_jobCancellations.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateEntriesAsync(
        IProgress<DeduplicationProgress>? progress,
        int totalRecords,
        int startOffset,
        CancellationToken cancellationToken)
    {
        const int batchSize = 1000;
        var processed = 0;
        var groupsCreated = 0;
        var recordsLinked = 0;
        var duplicateGroups = 0;

        // Group entries by time windows and similar values
        var entries = await _context.Entries
            .OrderBy(e => e.Mills)
            .Select(e => new { e.Id, e.Mills, e.Sgv, e.Mgdl, e.Type, e.DataSource })
            .ToListAsync(cancellationToken);

        var groupedByTime = new Dictionary<long, List<(Guid Id, double Glucose, string? Type, string? DataSource)>>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var windowKey = entry.Mills / MatchingWindowMillis;
            var glucose = entry.Sgv ?? entry.Mgdl;

            if (!groupedByTime.ContainsKey(windowKey))
                groupedByTime[windowKey] = new();

            groupedByTime[windowKey].Add((entry.Id, glucose, entry.Type, entry.DataSource));
        }

        // Process each time window
        foreach (var (windowKey, windowEntries) in groupedByTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Group by similar glucose values within the window
            var glucoseGroups = windowEntries
                .GroupBy(e => Math.Round(e.Glucose / 5) * 5) // Group within ±5 mg/dL
                .Where(g => g.Count() > 0);

            foreach (var glucoseGroup in glucoseGroups)
            {
                var groupEntries = glucoseGroup.ToList();

                if (groupEntries.Count > 1)
                {
                    duplicateGroups++;
                }

                var canonicalId = Guid.CreateVersion7();
                groupsCreated++;

                foreach (var entry in groupEntries)
                {
                    // Check if already linked
                    var existing = await _context.LinkedRecords
                        .AnyAsync(lr => lr.RecordType == "entry" && lr.RecordId == entry.Id, cancellationToken);

                    if (!existing)
                    {
                        var linkedRecord = new LinkedRecordEntity
                        {
                            CanonicalId = canonicalId,
                            RecordType = "entry",
                            RecordId = entry.Id,
                            SourceTimestamp = windowKey * MatchingWindowMillis,
                            DataSource = entry.DataSource ?? "unknown",
                            IsPrimary = entry == groupEntries.First()
                        };
                        _context.LinkedRecords.Add(linkedRecord);
                        recordsLinked++;
                    }

                    processed++;
                }

                if (processed % batchSize == 0)
                {
                    await _context.SaveChangesAsync(cancellationToken);
                    progress?.Report(new DeduplicationProgress
                    {
                        TotalRecords = totalRecords,
                        ProcessedRecords = startOffset + processed,
                        GroupsFound = groupsCreated,
                        RecordsLinked = recordsLinked,
                        CurrentPhase = "Entries"
                    });
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateTreatmentsAsync(
        IProgress<DeduplicationProgress>? progress,
        int totalRecords,
        int startOffset,
        CancellationToken cancellationToken)
    {
        const int batchSize = 500;
        var processed = 0;
        var groupsCreated = 0;
        var recordsLinked = 0;
        var duplicateGroups = 0;

        var treatments = await _context.Treatments
            .OrderBy(t => t.Mills)
            .Select(t => new { t.Id, t.Mills, t.EventType, t.Insulin, t.Carbs, Rate = t.Basal.Rate, t.DataSource })
            .ToListAsync(cancellationToken);

        var groupedByTime = new Dictionary<long, List<(Guid Id, string? EventType, double? Insulin, double? Carbs, double? Rate, string? DataSource)>>();

        foreach (var treatment in treatments)
        {
            var windowKey = treatment.Mills / MatchingWindowMillis;

            if (!groupedByTime.ContainsKey(windowKey))
                groupedByTime[windowKey] = new();

            groupedByTime[windowKey].Add((treatment.Id, treatment.EventType, treatment.Insulin, treatment.Carbs, treatment.Rate, treatment.DataSource));
        }

        foreach (var (windowKey, windowTreatments) in groupedByTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Group by event type within the window, but treat basal-related types as a single group
            var eventTypeGroups = windowTreatments
                .GroupBy(t => GetDeduplicationGroupKey(t.EventType))
                .Where(g => g.Count() > 0);

            foreach (var eventGroup in eventTypeGroups)
            {
                // Check if this is a basal-related group
                var isBasalGroup = eventGroup.Key == "__basal_group__";

                // Further group by similar values
                // For basal-related types, we only use Rate (not Insulin, since Insulin is calculated from Rate*Duration)
                // For other types, we use Insulin and Carbs
                var valueGroups = eventGroup
                    .GroupBy(t =>
                    {
                        if (isBasalGroup)
                        {
                            // For basals, only group by rate (ignore calculated Insulin)
                            var rateKey = t.Rate.HasValue ? Math.Round(t.Rate.Value * 20) / 20 : 0; // ±0.05 u/hr
                            return (0.0, 0.0, rateKey);
                        }
                        else
                        {
                            // For non-basals, group by insulin and carbs
                            var insulinKey = t.Insulin.HasValue ? Math.Round(t.Insulin.Value * 20) / 20 : 0; // ±0.05 units
                            var carbsKey = t.Carbs.HasValue ? Math.Round(t.Carbs.Value) : 0; // ±1g
                            return (insulinKey, carbsKey, 0.0);
                        }
                    });

                foreach (var valueGroup in valueGroups)
                {
                    // Sort by priority so higher priority types (e.g., Temp Basal) come first
                    var groupTreatments = valueGroup
                        .OrderByDescending(t => GetBasalTypePriority(t.EventType))
                        .ThenBy(t => t.Id) // Stable sort for non-basal types
                        .ToList();

                    if (groupTreatments.Count > 1)
                    {
                        duplicateGroups++;
                    }

                    var canonicalId = Guid.CreateVersion7();
                    groupsCreated++;

                    foreach (var treatment in groupTreatments)
                    {
                        var existing = await _context.LinkedRecords
                            .AnyAsync(lr => lr.RecordType == "treatment" && lr.RecordId == treatment.Id, cancellationToken);

                        if (!existing)
                        {
                            var linkedRecord = new LinkedRecordEntity
                            {
                                CanonicalId = canonicalId,
                                RecordType = "treatment",
                                RecordId = treatment.Id,
                                SourceTimestamp = windowKey * MatchingWindowMillis,
                                DataSource = treatment.DataSource ?? "unknown",
                                IsPrimary = treatment == groupTreatments.First()
                            };
                            _context.LinkedRecords.Add(linkedRecord);
                            recordsLinked++;
                        }

                        processed++;
                    }

                    if (processed % batchSize == 0)
                    {
                        await _context.SaveChangesAsync(cancellationToken);
                        progress?.Report(new DeduplicationProgress
                        {
                            TotalRecords = totalRecords,
                            ProcessedRecords = startOffset + processed,
                            GroupsFound = groupsCreated,
                            RecordsLinked = recordsLinked,
                            CurrentPhase = "Treatments"
                        });
                    }
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateStateSpansAsync(
        IProgress<DeduplicationProgress>? progress,
        int totalRecords,
        int startOffset,
        CancellationToken cancellationToken)
    {
        const int batchSize = 500;
        var processed = 0;
        var groupsCreated = 0;
        var recordsLinked = 0;
        var duplicateGroups = 0;

        var stateSpans = await _context.StateSpans
            .OrderBy(s => s.StartMills)
            .Select(s => new { s.Id, s.StartMills, s.Category, s.State, s.Source })
            .ToListAsync(cancellationToken);

        var groupedByTime = new Dictionary<long, List<(Guid Id, string? Category, string? State, string? Source)>>();

        foreach (var span in stateSpans)
        {
            var windowKey = span.StartMills / MatchingWindowMillis;

            if (!groupedByTime.ContainsKey(windowKey))
                groupedByTime[windowKey] = new();

            groupedByTime[windowKey].Add((span.Id, span.Category, span.State, span.Source));
        }

        foreach (var (windowKey, windowSpans) in groupedByTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Group by category and state within the window
            var categoryStateGroups = windowSpans
                .GroupBy(s => (s.Category ?? "unknown", s.State ?? "unknown"));

            foreach (var group in categoryStateGroups)
            {
                var groupSpans = group.ToList();

                if (groupSpans.Count > 1)
                {
                    duplicateGroups++;
                }

                var canonicalId = Guid.CreateVersion7();
                groupsCreated++;

                foreach (var span in groupSpans)
                {
                    var existing = await _context.LinkedRecords
                        .AnyAsync(lr => lr.RecordType == "statespan" && lr.RecordId == span.Id, cancellationToken);

                    if (!existing)
                    {
                        var linkedRecord = new LinkedRecordEntity
                        {
                            CanonicalId = canonicalId,
                            RecordType = "statespan",
                            RecordId = span.Id,
                            SourceTimestamp = windowKey * MatchingWindowMillis,
                            DataSource = span.Source ?? "unknown",
                            IsPrimary = span == groupSpans.First()
                        };
                        _context.LinkedRecords.Add(linkedRecord);
                        recordsLinked++;
                    }

                    processed++;
                }

                if (processed % batchSize == 0)
                {
                    await _context.SaveChangesAsync(cancellationToken);
                    progress?.Report(new DeduplicationProgress
                    {
                        TotalRecords = totalRecords,
                        ProcessedRecords = startOffset + processed,
                        GroupsFound = groupsCreated,
                        RecordsLinked = recordsLinked,
                        CurrentPhase = "StateSpans"
                    });
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    private static Entry MergeEntries(List<Entry> entries, Guid canonicalId)
    {
        if (entries.Count == 0)
            throw new ArgumentException("Cannot merge empty list of entries");

        var primary = entries[0];
        var merged = new Entry
        {
            Id = primary.Id,
            Mills = primary.Mills,
            DateString = primary.DateString,
            Mgdl = primary.Mgdl,
            Mmol = primary.Mmol,
            Sgv = primary.Sgv,
            Direction = primary.Direction,
            Trend = primary.Trend,
            TrendRate = primary.TrendRate,
            Type = primary.Type,
            Device = primary.Device,
            Notes = primary.Notes,
            Delta = primary.Delta,
            Scaled = primary.Scaled,
            Noise = primary.Noise,
            Filtered = primary.Filtered,
            Unfiltered = primary.Unfiltered,
            Rssi = primary.Rssi,
            Slope = primary.Slope,
            Intercept = primary.Intercept,
            Scale = primary.Scale,
            DataSource = primary.DataSource,
            Meta = primary.Meta != null ? new Dictionary<string, object>(primary.Meta) : new(),
            CanonicalId = canonicalId,
            Sources = entries.Select(e => e.DataSource).Where(s => s != null).Distinct().ToArray()!
        };

        // Enrich with data from other sources
        foreach (var entry in entries.Skip(1))
        {
            merged.Direction ??= entry.Direction;
            merged.Trend ??= entry.Trend;
            merged.TrendRate ??= entry.TrendRate;
            merged.Delta ??= entry.Delta;
            merged.Noise ??= entry.Noise;
            merged.Filtered ??= entry.Filtered;
            merged.Unfiltered ??= entry.Unfiltered;
            merged.Rssi ??= entry.Rssi;
            merged.Slope ??= entry.Slope;
            merged.Intercept ??= entry.Intercept;
            merged.Scale ??= entry.Scale;
            merged.Notes ??= entry.Notes;

            // Merge metadata
            if (entry.Meta != null)
            {
                foreach (var kvp in entry.Meta)
                {
                    merged.Meta.TryAdd(kvp.Key, kvp.Value);
                }
            }
        }

        return merged;
    }

    private static Treatment MergeTreatments(List<Treatment> treatments, Guid canonicalId)
    {
        if (treatments.Count == 0)
            throw new ArgumentException("Cannot merge empty list of treatments");

        // For basal-related treatments, prefer the highest priority type (e.g., Temp Basal over Basal)
        var primary = treatments[0];
        var preferredEventType = GetPreferredEventType(treatments);

        // When the preferred event type differs from the primary (e.g., Temp Basal preferred but
        // Basal is first by timestamp), use basal-related fields from the preferred-type treatment
        // so Duration/Percent/Rate come from the correct source.
        var basalSource = primary;
        if (preferredEventType != null && preferredEventType != primary.EventType)
        {
            basalSource = treatments.FirstOrDefault(t => t.EventType == preferredEventType) ?? primary;
        }

        var merged = new Treatment
        {
            Id = primary.Id,
            Mills = primary.Mills,
            Created_at = primary.Created_at,
            EventType = preferredEventType,
            Insulin = primary.Insulin,
            Carbs = primary.Carbs,
            Protein = primary.Protein,
            Fat = primary.Fat,
            Duration = basalSource.Duration,
            EnteredBy = primary.EnteredBy,
            Notes = primary.Notes,
            Reason = primary.Reason,
            Glucose = primary.Glucose,
            GlucoseType = primary.GlucoseType,
            Profile = primary.Profile,
            Percent = basalSource.Percent,
            Rate = basalSource.Rate,
            DataSource = primary.DataSource,
            AdditionalProperties = primary.AdditionalProperties != null
                ? new Dictionary<string, object>(primary.AdditionalProperties)
                : new(),
            CanonicalId = canonicalId,
            Sources = treatments.Select(t => t.DataSource).Where(s => s != null).Distinct().ToArray()!
        };

        // Enrich with data from other sources
        foreach (var treatment in treatments.Skip(1))
        {
            merged.Notes ??= treatment.Notes;
            merged.Reason ??= treatment.Reason;
            merged.Glucose ??= treatment.Glucose;
            merged.GlucoseType ??= treatment.GlucoseType;
            merged.Profile ??= treatment.Profile;
            merged.Protein ??= treatment.Protein;
            merged.Fat ??= treatment.Fat;

            // Enrich basal-related fields
            merged.Duration ??= treatment.Duration;
            merged.Percent ??= treatment.Percent;
            merged.Rate ??= treatment.Rate;
            merged.Carbs ??= treatment.Carbs;
            merged.Insulin ??= treatment.Insulin;

            // Merge additional properties
            if (treatment.AdditionalProperties != null)
            {
                foreach (var kvp in treatment.AdditionalProperties)
                {
                    merged.AdditionalProperties.TryAdd(kvp.Key, kvp.Value);
                }
            }
        }

        return merged;
    }

    private static StateSpan MergeStateSpans(List<StateSpan> stateSpans, Guid canonicalId)
    {
        if (stateSpans.Count == 0)
            throw new ArgumentException("Cannot merge empty list of state spans");

        var primary = stateSpans[0];
        var merged = new StateSpan
        {
            Id = primary.Id,
            Category = primary.Category,
            State = primary.State,
            StartMills = primary.StartMills,
            EndMills = primary.EndMills,
            Source = primary.Source,
            OriginalId = primary.OriginalId,
            Metadata = primary.Metadata != null
                ? new Dictionary<string, object>(primary.Metadata)
                : new(),
            CanonicalId = canonicalId,
            Sources = stateSpans.Select(s => s.Source).Where(s => s != null).Distinct().ToArray()!
        };

        // Enrich with data from other sources
        foreach (var span in stateSpans.Skip(1))
        {
            // If one source has end time and merged doesn't, take the end time
            if (!merged.EndMills.HasValue && span.EndMills.HasValue)
            {
                merged.EndMills = span.EndMills;
            }

            // Merge metadata
            if (span.Metadata != null)
            {
                foreach (var kvp in span.Metadata)
                {
                    merged.Metadata.TryAdd(kvp.Key, kvp.Value);
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// Gets the deduplication group key for an event type.
    /// Basal-related types are grouped together under a common key.
    /// </summary>
    private static string GetDeduplicationGroupKey(string? eventType)
    {
        if (string.IsNullOrEmpty(eventType))
            return "unknown";

        // Group all basal-related types together
        if (BasalRelatedTypes.Contains(eventType))
            return "__basal_group__";

        return eventType;
    }

    /// <summary>
    /// Gets the priority for a basal-related type.
    /// Higher values indicate higher priority (preferred when deduplicating).
    /// </summary>
    private static int GetBasalTypePriority(string? eventType)
    {
        if (string.IsNullOrEmpty(eventType))
            return -1;

        return BasalTypePriority.TryGetValue(eventType, out var priority) ? priority : -1;
    }

    /// <summary>
    /// Gets the preferred event type when merging treatments.
    /// For basal-related types, returns the highest priority type among all treatments.
    /// For other types, returns the primary treatment's event type.
    /// </summary>
    private static string? GetPreferredEventType(List<Treatment> treatments)
    {
        if (treatments.Count == 0)
            return null;

        var primary = treatments[0];

        // Check if any treatment is a basal-related type
        var basalTypes = treatments
            .Where(t => !string.IsNullOrEmpty(t.EventType) && BasalRelatedTypes.Contains(t.EventType))
            .Select(t => t.EventType!)
            .Distinct()
            .ToList();

        if (basalTypes.Count == 0)
        {
            // No basal-related types, use primary's event type
            return primary.EventType;
        }

        // Return the highest priority basal type
        return basalTypes
            .OrderByDescending(GetBasalTypePriority)
            .First();
    }
}
