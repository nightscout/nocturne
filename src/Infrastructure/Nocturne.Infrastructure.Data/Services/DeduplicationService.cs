using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
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

        // For state spans, check category and state
        if (recordType == RecordType.StateSpan && criteria.Category.HasValue)
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
        // For sensor glucose, check glucose value matching
        else if (recordType == RecordType.SensorGlucose && criteria.GlucoseValue.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var readings = await _context.SensorGlucose
                    .Where(r => recordIds.Contains(r.Id))
                    .ToListAsync(cancellationToken);

                foreach (var reading in readings)
                {
                    if (Math.Abs(reading.Mgdl - criteria.GlucoseValue.Value) <= criteria.GlucoseTolerance)
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For boluses, check insulin value matching
        else if (recordType == RecordType.Bolus && criteria.Insulin.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var boluses = await _context.Boluses
                    .Where(b => recordIds.Contains(b.Id))
                    .ToListAsync(cancellationToken);

                foreach (var bolus in boluses)
                {
                    if (Math.Abs(bolus.Insulin - criteria.Insulin.Value) <= criteria.InsulinTolerance)
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For carb intakes, check carbs value matching
        else if (recordType == RecordType.CarbIntake && criteria.Carbs.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var carbs = await _context.CarbIntakes
                    .Where(c => recordIds.Contains(c.Id))
                    .ToListAsync(cancellationToken);

                foreach (var carb in carbs)
                {
                    if (Math.Abs(carb.Carbs - criteria.Carbs.Value) <= criteria.CarbsTolerance)
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For BG checks, check glucose value matching
        else if (recordType == RecordType.BGCheck && criteria.GlucoseValue.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var bgChecks = await _context.BGChecks
                    .Where(bg => recordIds.Contains(bg.Id))
                    .ToListAsync(cancellationToken);

                foreach (var bg in bgChecks)
                {
                    if (Math.Abs(bg.Glucose - criteria.GlucoseValue.Value) <= criteria.GlucoseTolerance)
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For device events, check event type matching
        else if (recordType == RecordType.DeviceEvent && !string.IsNullOrEmpty(criteria.EventType))
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var events = await _context.DeviceEvents
                    .Where(e => recordIds.Contains(e.Id))
                    .ToListAsync(cancellationToken);

                foreach (var e in events)
                {
                    if (string.Equals(e.EventType, criteria.EventType, StringComparison.OrdinalIgnoreCase))
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For notes, time-window only matching
        else if (recordType == RecordType.Note)
        {
            if (potentialMatches.Count > 0)
            {
                return potentialMatches.First().CanonicalId;
            }
        }
        // For bolus calculations, check carb input matching
        else if (recordType == RecordType.BolusCalculation && criteria.Carbs.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var calcs = await _context.BolusCalculations
                    .Where(bc => recordIds.Contains(bc.Id))
                    .ToListAsync(cancellationToken);

                foreach (var bc in calcs)
                {
                    if (Math.Abs((bc.CarbInput ?? 0) - criteria.Carbs.Value) <= criteria.CarbsTolerance)
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For temp basals, check rate and origin matching
        else if (recordType == RecordType.TempBasal && criteria.Rate.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var tempBasals = await _context.TempBasals
                    .Where(tb => recordIds.Contains(tb.Id))
                    .ToListAsync(cancellationToken);

                foreach (var tb in tempBasals)
                {
                    if (Math.Abs(tb.Rate - criteria.Rate.Value) <= criteria.RateTolerance)
                    {
                        return canonicalId;
                    }
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

        // If the existing primary references a record that no longer exists,
        // clean up the orphaned entries and promote this record to primary.
        if (!isPrimary && existingInGroup is { IsPrimary: true })
        {
            var primaryExists = await RecordExistsAsync(recordTypeStr, existingInGroup.RecordId, cancellationToken);
            if (!primaryExists)
            {
                // Remove all orphaned linked records in this group
                var orphaned = await _context.LinkedRecords
                    .Where(lr => lr.CanonicalId == canonicalId)
                    .ToListAsync(cancellationToken);
                var orphanedIds = orphaned.Select(lr => lr.RecordId).ToHashSet();

                foreach (var o in orphaned)
                {
                    var exists = orphanedIds.Contains(recordId) && o.RecordId == recordId
                        ? true // The record we're about to link obviously exists
                        : await RecordExistsAsync(recordTypeStr, o.RecordId, cancellationToken);
                    if (!exists)
                    {
                        _context.LinkedRecords.Remove(o);
                    }
                }

                isPrimary = true;
                _logger.LogDebug(
                    "Promoted {RecordType} {RecordId} to primary after orphaned primary cleanup in canonical {CanonicalId}",
                    recordType, recordId, canonicalId);
            }
        }

        // If this is the new primary, demote the old primary
        if (isPrimary && existingInGroup != null && _context.Entry(existingInGroup).State != Microsoft.EntityFrameworkCore.EntityState.Deleted)
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

    private async Task<bool> RecordExistsAsync(string recordType, Guid recordId, CancellationToken ct)
    {
        return recordType switch
        {
            "bolus" => await _context.Boluses.AnyAsync(b => b.Id == recordId, ct),
            "carbintake" => await _context.CarbIntakes.AnyAsync(c => c.Id == recordId, ct),
            "sensorglucose" => await _context.SensorGlucose.AnyAsync(s => s.Id == recordId, ct),
            "tempbasal" => await _context.TempBasals.AnyAsync(t => t.Id == recordId, ct),
            "bgcheck" => await _context.BGChecks.AnyAsync(b => b.Id == recordId, ct),
            "deviceevent" => await _context.DeviceEvents.AnyAsync(d => d.Id == recordId, ct),
            "note" => await _context.Notes.AnyAsync(n => n.Id == recordId, ct),
            "boluscalculation" => await _context.BolusCalculations.AnyAsync(b => b.Id == recordId, ct),
            _ => true // Assume exists for unknown types to avoid accidental promotion
        };
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
            .OrderBy(s => s.StartTimestamp)
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
            var treatmentCount = 0;
            var stateSpanCount = await _context.StateSpans.CountAsync(cancellationToken);
            var sensorGlucoseCount = await _context.SensorGlucose.CountAsync(cancellationToken);
            var meterGlucoseCount = await _context.MeterGlucose.CountAsync(cancellationToken);
            var bolusCount = await _context.Boluses.CountAsync(cancellationToken);
            var carbIntakeCount = await _context.CarbIntakes.CountAsync(cancellationToken);
            var bgCheckCount = await _context.BGChecks.CountAsync(cancellationToken);
            var deviceEventCount = await _context.DeviceEvents.CountAsync(cancellationToken);
            var noteCount = await _context.Notes.CountAsync(cancellationToken);
            var bolusCalcCount = await _context.BolusCalculations.CountAsync(cancellationToken);
            var tempBasalCount = await _context.TempBasals.CountAsync(cancellationToken);
            var totalRecords = treatmentCount + stateSpanCount
                + sensorGlucoseCount + meterGlucoseCount + bolusCount + carbIntakeCount + bgCheckCount
                + deviceEventCount + noteCount + bolusCalcCount + tempBasalCount;

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

            // Process sensor glucose
            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = processed,
                GroupsFound = groupsCreated,
                RecordsLinked = recordsLinked,
                CurrentPhase = "SensorGlucose"
            });

            var sensorGlucoseResult = await DeduplicateSensorGlucoseAsync(progress, totalRecords, processed, cancellationToken);
            processed += sensorGlucoseResult.processed;
            groupsCreated += sensorGlucoseResult.groups;
            recordsLinked += sensorGlucoseResult.linked;
            duplicateGroups += sensorGlucoseResult.duplicates;

            // Process boluses
            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = processed,
                GroupsFound = groupsCreated,
                RecordsLinked = recordsLinked,
                CurrentPhase = "Boluses"
            });

            var bolusResult = await DeduplicateBolusesAsync(progress, totalRecords, processed, cancellationToken);
            processed += bolusResult.processed;
            groupsCreated += bolusResult.groups;
            recordsLinked += bolusResult.linked;
            duplicateGroups += bolusResult.duplicates;

            // Process carb intakes
            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = processed,
                GroupsFound = groupsCreated,
                RecordsLinked = recordsLinked,
                CurrentPhase = "CarbIntakes"
            });

            var carbIntakeResult = await DeduplicateCarbIntakesAsync(progress, totalRecords, processed, cancellationToken);
            processed += carbIntakeResult.processed;
            groupsCreated += carbIntakeResult.groups;
            recordsLinked += carbIntakeResult.linked;
            duplicateGroups += carbIntakeResult.duplicates;

            // Process BG checks
            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = processed,
                GroupsFound = groupsCreated,
                RecordsLinked = recordsLinked,
                CurrentPhase = "BGChecks"
            });

            var bgCheckResult = await DeduplicateBGChecksAsync(progress, totalRecords, processed, cancellationToken);
            processed += bgCheckResult.processed;
            groupsCreated += bgCheckResult.groups;
            recordsLinked += bgCheckResult.linked;
            duplicateGroups += bgCheckResult.duplicates;

            // Process device events
            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = processed,
                GroupsFound = groupsCreated,
                RecordsLinked = recordsLinked,
                CurrentPhase = "DeviceEvents"
            });

            var deviceEventResult = await DeduplicateDeviceEventsAsync(progress, totalRecords, processed, cancellationToken);
            processed += deviceEventResult.processed;
            groupsCreated += deviceEventResult.groups;
            recordsLinked += deviceEventResult.linked;
            duplicateGroups += deviceEventResult.duplicates;

            // Process notes
            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = processed,
                GroupsFound = groupsCreated,
                RecordsLinked = recordsLinked,
                CurrentPhase = "Notes"
            });

            var noteResult = await DeduplicateNotesAsync(progress, totalRecords, processed, cancellationToken);
            processed += noteResult.processed;
            groupsCreated += noteResult.groups;
            recordsLinked += noteResult.linked;
            duplicateGroups += noteResult.duplicates;

            // Process bolus calculations
            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = processed,
                GroupsFound = groupsCreated,
                RecordsLinked = recordsLinked,
                CurrentPhase = "BolusCalculations"
            });

            var bolusCalcResult = await DeduplicateBolusCalculationsAsync(progress, totalRecords, processed, cancellationToken);
            processed += bolusCalcResult.processed;
            groupsCreated += bolusCalcResult.groups;
            recordsLinked += bolusCalcResult.linked;
            duplicateGroups += bolusCalcResult.duplicates;

            // Process temp basals
            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = processed,
                GroupsFound = groupsCreated,
                RecordsLinked = recordsLinked,
                CurrentPhase = "TempBasals"
            });

            var tempBasalResult = await DeduplicateTempBasalsAsync(progress, totalRecords, processed, cancellationToken);
            processed += tempBasalResult.processed;
            groupsCreated += tempBasalResult.groups;
            recordsLinked += tempBasalResult.linked;
            duplicateGroups += tempBasalResult.duplicates;

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
                TreatmentsProcessed = 0,
                StateSpansProcessed = stateSpanResult.processed,
                SensorGlucoseProcessed = sensorGlucoseResult.processed,
                BolusesProcessed = bolusResult.processed,
                CarbIntakesProcessed = carbIntakeResult.processed,
                BGChecksProcessed = bgCheckResult.processed,
                DeviceEventsProcessed = deviceEventResult.processed,
                NotesProcessed = noteResult.processed,
                BolusCalculationsProcessed = bolusCalcResult.processed,
                TempBasalsProcessed = tempBasalResult.processed,
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

        // Step 1: Materialize from DB with just the fields we need
        var sgRaw = await _context.SensorGlucose
            .Select(e => new { e.Id, e.Timestamp, Glucose = e.Mgdl, e.DataSource })
            .ToListAsync(cancellationToken);
        var mgRaw = await _context.MeterGlucose
            .Select(e => new { e.Id, e.Timestamp, Glucose = e.Mgdl, e.DataSource })
            .ToListAsync(cancellationToken);

        // Step 2: Compute mills in memory and combine
        var allRecords = sgRaw.Select(e => (e.Id, Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(), e.Glucose, Type: "sgv", e.DataSource))
            .Concat(mgRaw.Select(e => (e.Id, Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(), e.Glucose, Type: "mbg", e.DataSource)))
            .OrderBy(e => e.Mills)
            .ToList();

        var groupedByTime = new Dictionary<long, List<(Guid Id, double Glucose, string Type, string? DataSource)>>();

        foreach (var record in allRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var windowKey = record.Mills / MatchingWindowMillis;

            if (!groupedByTime.ContainsKey(windowKey))
                groupedByTime[windowKey] = new();

            groupedByTime[windowKey].Add((record.Id, record.Glucose, record.Type, record.DataSource));
        }

        // Process each time window
        foreach (var (windowKey, windowEntries) in groupedByTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Group by similar glucose values within the window
            var glucoseGroups = windowEntries
                .GroupBy(e => Math.Round(e.Glucose / 5) * 5) // Group within +/-5 mg/dL
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
                    var recordType = entry.Type == "sgv" ? "sensorglucose" : "meterglucose";

                    // Check if already linked
                    var existing = await _context.LinkedRecords
                        .AnyAsync(lr => lr.RecordType == recordType && lr.RecordId == entry.Id, cancellationToken);

                    if (!existing)
                    {
                        var linkedRecord = new LinkedRecordEntity
                        {
                            CanonicalId = canonicalId,
                            RecordType = recordType,
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
            .OrderBy(s => s.StartTimestamp)
            .Select(s => new { s.Id, s.StartTimestamp, s.Category, s.State, s.Source })
            .ToListAsync(cancellationToken);

        // Track which spans have been processed to avoid duplicates
        var processedSpans = new HashSet<Guid>();

        // Process spans in order, looking for matches within the time window
        foreach (var span in stateSpans)
        {
            if (processedSpans.Contains(span.Id))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            // Find all spans within the matching window that have the same category and state
            var windowStart = new DateTimeOffset(span.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() - MatchingWindowMillis;
            var windowEnd = new DateTimeOffset(span.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() + MatchingWindowMillis;

            var matches = stateSpans
                .Where(s => !processedSpans.Contains(s.Id))
                .Where(s => new DateTimeOffset(s.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() >= windowStart && new DateTimeOffset(s.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() <= windowEnd)
                .Where(s => string.Equals(s.Category, span.Category, StringComparison.OrdinalIgnoreCase))
                .Where(s => string.Equals(s.State, span.State, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                continue;

            if (matches.Count > 1)
            {
                duplicateGroups++;
            }

            var canonicalId = Guid.CreateVersion7();
            groupsCreated++;

            foreach (var match in matches)
            {
                processedSpans.Add(match.Id);

                var existing = await _context.LinkedRecords
                    .AnyAsync(lr => lr.RecordType == "statespan" && lr.RecordId == match.Id, cancellationToken);

                if (!existing)
                {
                    var linkedRecord = new LinkedRecordEntity
                    {
                        CanonicalId = canonicalId,
                        RecordType = "statespan",
                        RecordId = match.Id,
                        SourceTimestamp = new DateTimeOffset(match.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                        DataSource = match.Source ?? "unknown",
                        IsPrimary = match == matches.First()
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

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateSensorGlucoseAsync(
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

        var readings = await _context.SensorGlucose
            .OrderBy(r => r.Timestamp)
            .Select(r => new { r.Id, r.Timestamp, r.Mgdl, r.DataSource })
            .ToListAsync(cancellationToken);

        var groupedByTime = new Dictionary<long, List<(Guid Id, double Mgdl, string? DataSource)>>();

        foreach (var reading in readings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowKey = new DateTimeOffset(reading.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() / MatchingWindowMillis;

            if (!groupedByTime.ContainsKey(windowKey))
                groupedByTime[windowKey] = new();

            groupedByTime[windowKey].Add((reading.Id, reading.Mgdl, reading.DataSource));
        }

        foreach (var (windowKey, windowReadings) in groupedByTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var glucoseGroups = windowReadings
                .GroupBy(r => Math.Round(r.Mgdl))
                .Where(g => g.Count() > 0);

            foreach (var glucoseGroup in glucoseGroups)
            {
                var groupItems = glucoseGroup.ToList();

                if (groupItems.Count > 1)
                    duplicateGroups++;

                var canonicalId = Guid.CreateVersion7();
                groupsCreated++;

                foreach (var item in groupItems)
                {
                    var existing = await _context.LinkedRecords
                        .AnyAsync(lr => lr.RecordType == "sensorglucose" && lr.RecordId == item.Id, cancellationToken);

                    if (!existing)
                    {
                        var linkedRecord = new LinkedRecordEntity
                        {
                            CanonicalId = canonicalId,
                            RecordType = "sensorglucose",
                            RecordId = item.Id,
                            SourceTimestamp = windowKey * MatchingWindowMillis,
                            DataSource = item.DataSource ?? "unknown",
                            IsPrimary = item == groupItems.First()
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
                        CurrentPhase = "SensorGlucose"
                    });
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateBolusesAsync(
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

        var boluses = await _context.Boluses
            .OrderBy(b => b.Timestamp)
            .Select(b => new { b.Id, b.Timestamp, b.Insulin, b.DataSource })
            .ToListAsync(cancellationToken);

        var groupedByTime = new Dictionary<long, List<(Guid Id, double Insulin, string? DataSource)>>();

        foreach (var bolus in boluses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowKey = new DateTimeOffset(bolus.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() / MatchingWindowMillis;

            if (!groupedByTime.ContainsKey(windowKey))
                groupedByTime[windowKey] = new();

            groupedByTime[windowKey].Add((bolus.Id, bolus.Insulin, bolus.DataSource));
        }

        foreach (var (windowKey, windowBoluses) in groupedByTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var insulinGroups = windowBoluses
                .GroupBy(b => Math.Round(b.Insulin * 20) / 20)
                .Where(g => g.Count() > 0);

            foreach (var insulinGroup in insulinGroups)
            {
                var groupItems = insulinGroup.ToList();

                if (groupItems.Count > 1)
                    duplicateGroups++;

                var canonicalId = Guid.CreateVersion7();
                groupsCreated++;

                foreach (var item in groupItems)
                {
                    var existing = await _context.LinkedRecords
                        .AnyAsync(lr => lr.RecordType == "bolus" && lr.RecordId == item.Id, cancellationToken);

                    if (!existing)
                    {
                        var linkedRecord = new LinkedRecordEntity
                        {
                            CanonicalId = canonicalId,
                            RecordType = "bolus",
                            RecordId = item.Id,
                            SourceTimestamp = windowKey * MatchingWindowMillis,
                            DataSource = item.DataSource ?? "unknown",
                            IsPrimary = item == groupItems.First()
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
                        CurrentPhase = "Boluses"
                    });
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateCarbIntakesAsync(
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

        var carbIntakes = await _context.CarbIntakes
            .OrderBy(c => c.Timestamp)
            .Select(c => new { c.Id, c.Timestamp, c.Carbs, c.DataSource })
            .ToListAsync(cancellationToken);

        var groupedByTime = new Dictionary<long, List<(Guid Id, double Carbs, string? DataSource)>>();

        foreach (var carb in carbIntakes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowKey = new DateTimeOffset(carb.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() / MatchingWindowMillis;

            if (!groupedByTime.ContainsKey(windowKey))
                groupedByTime[windowKey] = new();

            groupedByTime[windowKey].Add((carb.Id, carb.Carbs, carb.DataSource));
        }

        foreach (var (windowKey, windowCarbs) in groupedByTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var carbGroups = windowCarbs
                .GroupBy(c => Math.Round(c.Carbs))
                .Where(g => g.Count() > 0);

            foreach (var carbGroup in carbGroups)
            {
                var groupItems = carbGroup.ToList();

                if (groupItems.Count > 1)
                    duplicateGroups++;

                var canonicalId = Guid.CreateVersion7();
                groupsCreated++;

                foreach (var item in groupItems)
                {
                    var existing = await _context.LinkedRecords
                        .AnyAsync(lr => lr.RecordType == "carbintake" && lr.RecordId == item.Id, cancellationToken);

                    if (!existing)
                    {
                        var linkedRecord = new LinkedRecordEntity
                        {
                            CanonicalId = canonicalId,
                            RecordType = "carbintake",
                            RecordId = item.Id,
                            SourceTimestamp = windowKey * MatchingWindowMillis,
                            DataSource = item.DataSource ?? "unknown",
                            IsPrimary = item == groupItems.First()
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
                        CurrentPhase = "CarbIntakes"
                    });
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateBGChecksAsync(
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

        var bgChecks = await _context.BGChecks
            .OrderBy(bg => bg.Timestamp)
            .Select(bg => new { bg.Id, bg.Timestamp, bg.Glucose, bg.DataSource })
            .ToListAsync(cancellationToken);

        var groupedByTime = new Dictionary<long, List<(Guid Id, double Glucose, string? DataSource)>>();

        foreach (var bg in bgChecks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowKey = new DateTimeOffset(bg.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() / MatchingWindowMillis;

            if (!groupedByTime.ContainsKey(windowKey))
                groupedByTime[windowKey] = new();

            groupedByTime[windowKey].Add((bg.Id, bg.Glucose, bg.DataSource));
        }

        foreach (var (windowKey, windowBGs) in groupedByTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var glucoseGroups = windowBGs
                .GroupBy(bg => Math.Round(bg.Glucose))
                .Where(g => g.Count() > 0);

            foreach (var glucoseGroup in glucoseGroups)
            {
                var groupItems = glucoseGroup.ToList();

                if (groupItems.Count > 1)
                    duplicateGroups++;

                var canonicalId = Guid.CreateVersion7();
                groupsCreated++;

                foreach (var item in groupItems)
                {
                    var existing = await _context.LinkedRecords
                        .AnyAsync(lr => lr.RecordType == "bgcheck" && lr.RecordId == item.Id, cancellationToken);

                    if (!existing)
                    {
                        var linkedRecord = new LinkedRecordEntity
                        {
                            CanonicalId = canonicalId,
                            RecordType = "bgcheck",
                            RecordId = item.Id,
                            SourceTimestamp = windowKey * MatchingWindowMillis,
                            DataSource = item.DataSource ?? "unknown",
                            IsPrimary = item == groupItems.First()
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
                        CurrentPhase = "BGChecks"
                    });
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateDeviceEventsAsync(
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

        var deviceEvents = await _context.DeviceEvents
            .OrderBy(e => e.Timestamp)
            .Select(e => new { e.Id, e.Timestamp, e.EventType, e.DataSource })
            .ToListAsync(cancellationToken);

        var groupedByTime = new Dictionary<long, List<(Guid Id, string EventType, string? DataSource)>>();

        foreach (var evt in deviceEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowKey = new DateTimeOffset(evt.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() / MatchingWindowMillis;

            if (!groupedByTime.ContainsKey(windowKey))
                groupedByTime[windowKey] = new();

            groupedByTime[windowKey].Add((evt.Id, evt.EventType, evt.DataSource));
        }

        foreach (var (windowKey, windowEvents) in groupedByTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var eventTypeGroups = windowEvents
                .GroupBy(e => e.EventType, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 0);

            foreach (var eventGroup in eventTypeGroups)
            {
                var groupItems = eventGroup.ToList();

                if (groupItems.Count > 1)
                    duplicateGroups++;

                var canonicalId = Guid.CreateVersion7();
                groupsCreated++;

                foreach (var item in groupItems)
                {
                    var existing = await _context.LinkedRecords
                        .AnyAsync(lr => lr.RecordType == "deviceevent" && lr.RecordId == item.Id, cancellationToken);

                    if (!existing)
                    {
                        var linkedRecord = new LinkedRecordEntity
                        {
                            CanonicalId = canonicalId,
                            RecordType = "deviceevent",
                            RecordId = item.Id,
                            SourceTimestamp = windowKey * MatchingWindowMillis,
                            DataSource = item.DataSource ?? "unknown",
                            IsPrimary = item == groupItems.First()
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
                        CurrentPhase = "DeviceEvents"
                    });
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateNotesAsync(
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

        var notes = await _context.Notes
            .OrderBy(n => n.Timestamp)
            .Select(n => new { n.Id, n.Timestamp, n.DataSource })
            .ToListAsync(cancellationToken);

        var groupedByTime = new Dictionary<long, List<(Guid Id, string? DataSource)>>();

        foreach (var note in notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowKey = new DateTimeOffset(note.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() / MatchingWindowMillis;

            if (!groupedByTime.ContainsKey(windowKey))
                groupedByTime[windowKey] = new();

            groupedByTime[windowKey].Add((note.Id, note.DataSource));
        }

        foreach (var (windowKey, windowNotes) in groupedByTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Notes use time-window only — all notes in same window form one group
            var groupItems = windowNotes;

            if (groupItems.Count > 1)
                duplicateGroups++;

            var canonicalId = Guid.CreateVersion7();
            groupsCreated++;

            foreach (var item in groupItems)
            {
                var existing = await _context.LinkedRecords
                    .AnyAsync(lr => lr.RecordType == "note" && lr.RecordId == item.Id, cancellationToken);

                if (!existing)
                {
                    var linkedRecord = new LinkedRecordEntity
                    {
                        CanonicalId = canonicalId,
                        RecordType = "note",
                        RecordId = item.Id,
                        SourceTimestamp = windowKey * MatchingWindowMillis,
                        DataSource = item.DataSource ?? "unknown",
                        IsPrimary = item == groupItems.First()
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
                    CurrentPhase = "Notes"
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateBolusCalculationsAsync(
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

        var calcs = await _context.BolusCalculations
            .OrderBy(bc => bc.Timestamp)
            .Select(bc => new { bc.Id, bc.Timestamp, bc.CarbInput, bc.DataSource })
            .ToListAsync(cancellationToken);

        var groupedByTime = new Dictionary<long, List<(Guid Id, double CarbInput, string? DataSource)>>();

        foreach (var calc in calcs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowKey = new DateTimeOffset(calc.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds() / MatchingWindowMillis;

            if (!groupedByTime.ContainsKey(windowKey))
                groupedByTime[windowKey] = new();

            groupedByTime[windowKey].Add((calc.Id, calc.CarbInput ?? 0, calc.DataSource));
        }

        foreach (var (windowKey, windowCalcs) in groupedByTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var carbGroups = windowCalcs
                .GroupBy(bc => Math.Round(bc.CarbInput))
                .Where(g => g.Count() > 0);

            foreach (var carbGroup in carbGroups)
            {
                var groupItems = carbGroup.ToList();

                if (groupItems.Count > 1)
                    duplicateGroups++;

                var canonicalId = Guid.CreateVersion7();
                groupsCreated++;

                foreach (var item in groupItems)
                {
                    var existing = await _context.LinkedRecords
                        .AnyAsync(lr => lr.RecordType == "boluscalculation" && lr.RecordId == item.Id, cancellationToken);

                    if (!existing)
                    {
                        var linkedRecord = new LinkedRecordEntity
                        {
                            CanonicalId = canonicalId,
                            RecordType = "boluscalculation",
                            RecordId = item.Id,
                            SourceTimestamp = windowKey * MatchingWindowMillis,
                            DataSource = item.DataSource ?? "unknown",
                            IsPrimary = item == groupItems.First()
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
                        CurrentPhase = "BolusCalculations"
                    });
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateTempBasalsAsync(
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

        var tempBasals = await _context.TempBasals
            .OrderBy(tb => tb.StartTimestamp)
            .Select(tb => new { tb.Id, tb.StartTimestamp, tb.Rate, tb.Origin, tb.DataSource })
            .ToListAsync(cancellationToken);

        // Track which records have been processed to avoid duplicates
        var processedIds = new HashSet<Guid>();

        foreach (var tempBasal in tempBasals)
        {
            if (processedIds.Contains(tempBasal.Id))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            var mills = new DateTimeOffset(tempBasal.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var windowStart = mills - MatchingWindowMillis;
            var windowEnd = mills + MatchingWindowMillis;

            // Find all temp basals within the matching window that have the same rate and origin
            var matches = tempBasals
                .Where(tb => !processedIds.Contains(tb.Id))
                .Where(tb =>
                {
                    var tbMills = new DateTimeOffset(tb.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    return tbMills >= windowStart && tbMills <= windowEnd;
                })
                .Where(tb => Math.Abs(tb.Rate - tempBasal.Rate) <= 0.05) // ±0.05 u/hr tolerance
                .Where(tb => string.Equals(tb.Origin, tempBasal.Origin, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                continue;

            if (matches.Count > 1)
            {
                duplicateGroups++;
            }

            var canonicalId = Guid.CreateVersion7();
            groupsCreated++;

            foreach (var match in matches)
            {
                processedIds.Add(match.Id);

                var existing = await _context.LinkedRecords
                    .AnyAsync(lr => lr.RecordType == "tempbasal" && lr.RecordId == match.Id, cancellationToken);

                if (!existing)
                {
                    var linkedRecord = new LinkedRecordEntity
                    {
                        CanonicalId = canonicalId,
                        RecordType = "tempbasal",
                        RecordId = match.Id,
                        SourceTimestamp = new DateTimeOffset(match.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                        DataSource = match.DataSource ?? "unknown",
                        IsPrimary = match == matches.First()
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
                    CurrentPhase = "TempBasals"
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (processed, groupsCreated, recordsLinked, duplicateGroups);
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
            StartTimestamp = primary.StartTimestamp,
            EndTimestamp = primary.EndTimestamp,
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
            if (!merged.EndTimestamp.HasValue && span.EndTimestamp.HasValue)
            {
                merged.EndTimestamp = span.EndTimestamp;
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
