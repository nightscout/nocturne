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

    /// <inheritdoc />
    public async Task<DeduplicationBatchResult> DeduplicateBatchAsync(
        RecordType recordType,
        IReadOnlyList<DeduplicationInput> records,
        CancellationToken ct = default)
    {
        if (records.Count == 0)
            return new DeduplicationBatchResult(0, 0, 0, 0);

        var recordTypeStr = recordType.ToString().ToLowerInvariant();

        // 1. Compute union time window
        var minMills = records.Min(r => r.Mills) - MatchingWindowMillis;
        var maxMills = records.Max(r => r.Mills) + MatchingWindowMillis;

        // 2. One query: all linked_records in the window for this type
        var allPotentialMatches = await _context.LinkedRecords
            .Where(lr => lr.RecordType == recordTypeStr)
            .Where(lr => lr.SourceTimestamp >= minMills && lr.SourceTimestamp <= maxMills)
            .ToListAsync(ct);

        // 3. One query: load type-specific matcher
        var referencedIds = allPotentialMatches.Select(m => m.RecordId).ToHashSet();
        var matcher = await LoadMatcherAsync(recordType, referencedIds, ct);

        // 4. One query: which input records are already linked?
        var inputIds = records.Select(r => r.RecordId).ToList();
        var alreadyLinked = (await _context.LinkedRecords
            .Where(lr => lr.RecordType == recordTypeStr && inputIds.Contains(lr.RecordId))
            .Select(lr => lr.RecordId)
            .ToListAsync(ct))
            .ToHashSet();

        // 5. In-memory matching + intra-batch canonical assignment
        var batchAssignments = new List<(DeduplicationInput input, Guid canonicalId)>();
        var newLinks = new List<LinkedRecordEntity>();
        var groupsCreated = 0;
        var duplicateGroups = 0;

        foreach (var record in records)
        {
            if (alreadyLinked.Contains(record.RecordId))
                continue;

            var windowStart = record.Mills - MatchingWindowMillis;
            var windowEnd = record.Mills + MatchingWindowMillis;

            // Check DB matches
            var myMatches = allPotentialMatches
                .Where(m => m.SourceTimestamp >= windowStart && m.SourceTimestamp <= windowEnd)
                .ToList();

            Guid? canonicalId = null;

            // Try to match against existing canonical groups
            foreach (var group in myMatches.GroupBy(m => m.CanonicalId))
            {
                if (group.Any(m => matcher(m.RecordId, record.Criteria)))
                {
                    canonicalId = group.Key;
                    duplicateGroups++;
                    break;
                }
            }

            // Try intra-batch matches
            if (canonicalId == null)
            {
                foreach (var (priorInput, priorCanonical) in batchAssignments)
                {
                    if (Math.Abs(priorInput.Mills - record.Mills) <= MatchingWindowMillis
                        && CriteriaMatch(recordType, priorInput.Criteria, record.Criteria))
                    {
                        canonicalId = priorCanonical;
                        duplicateGroups++;
                        break;
                    }
                }
            }

            if (canonicalId == null)
            {
                canonicalId = Guid.CreateVersion7();
                groupsCreated++;
            }

            batchAssignments.Add((record, canonicalId.Value));
            newLinks.Add(new LinkedRecordEntity
            {
                CanonicalId = canonicalId.Value,
                RecordType = recordTypeStr,
                RecordId = record.RecordId,
                SourceTimestamp = record.Mills,
                DataSource = record.DataSource,
                IsPrimary = !allPotentialMatches.Any(m => m.CanonicalId == canonicalId.Value)
                            && !newLinks.Any(l => l.CanonicalId == canonicalId.Value)
            });
        }

        // 6. Bulk insert
        if (newLinks.Count > 0)
        {
            _context.LinkedRecords.AddRange(newLinks);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        return new DeduplicationBatchResult(
            Processed: records.Count,
            GroupsCreated: groupsCreated,
            RecordsLinked: newLinks.Count,
            DuplicateGroups: duplicateGroups);
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

    private async Task<Func<Guid, MatchCriteria, bool>> LoadMatcherAsync(
        RecordType recordType, HashSet<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return (_, _) => false;

        switch (recordType)
        {
            case RecordType.TempBasal:
            {
                var records = (await _context.TempBasals
                    .Where(t => ids.Contains(t.Id))
                    .ToListAsync(ct))
                    .ToDictionary(t => t.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var tb) && criteria.Rate.HasValue
                    && Math.Abs(tb.Rate - criteria.Rate.Value) <= criteria.RateTolerance;
            }
            case RecordType.SensorGlucose:
            {
                var records = (await _context.SensorGlucose
                    .Where(s => ids.Contains(s.Id))
                    .ToListAsync(ct))
                    .ToDictionary(s => s.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var sg) && criteria.GlucoseValue.HasValue
                    && Math.Abs(sg.Mgdl - criteria.GlucoseValue.Value) <= criteria.GlucoseTolerance;
            }
            case RecordType.Bolus:
            {
                var records = (await _context.Boluses
                    .Where(b => ids.Contains(b.Id))
                    .ToListAsync(ct))
                    .ToDictionary(b => b.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var b) && criteria.Insulin.HasValue
                    && Math.Abs(b.Insulin - criteria.Insulin.Value) <= criteria.InsulinTolerance;
            }
            case RecordType.CarbIntake:
            {
                var records = (await _context.CarbIntakes
                    .Where(c => ids.Contains(c.Id))
                    .ToListAsync(ct))
                    .ToDictionary(c => c.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var c) && criteria.Carbs.HasValue
                    && Math.Abs(c.Carbs - criteria.Carbs.Value) <= criteria.CarbsTolerance;
            }
            case RecordType.BGCheck:
            {
                var records = (await _context.BGChecks
                    .Where(bg => ids.Contains(bg.Id))
                    .ToListAsync(ct))
                    .ToDictionary(bg => bg.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var bg) && criteria.GlucoseValue.HasValue
                    && Math.Abs(bg.Glucose - criteria.GlucoseValue.Value) <= criteria.GlucoseTolerance;
            }
            case RecordType.DeviceEvent:
            {
                var records = (await _context.DeviceEvents
                    .Where(d => ids.Contains(d.Id))
                    .ToListAsync(ct))
                    .ToDictionary(d => d.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var d) && !string.IsNullOrEmpty(criteria.EventType)
                    && string.Equals(d.EventType, criteria.EventType, StringComparison.OrdinalIgnoreCase);
            }
            case RecordType.Note:
                return (_, _) => true; // time-window only matching
            case RecordType.BolusCalculation:
            {
                var records = (await _context.BolusCalculations
                    .Where(bc => ids.Contains(bc.Id))
                    .ToListAsync(ct))
                    .ToDictionary(bc => bc.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var bc) && criteria.Carbs.HasValue
                    && Math.Abs((bc.CarbInput ?? 0) - criteria.Carbs.Value) <= criteria.CarbsTolerance;
            }
            case RecordType.StateSpan:
            {
                var records = (await _context.StateSpans
                    .Where(s => ids.Contains(s.Id))
                    .ToListAsync(ct))
                    .ToDictionary(s => s.Id);
                return (id, criteria) =>
                {
                    if (!records.TryGetValue(id, out var ss) || !criteria.Category.HasValue)
                        return false;
                    var categoryStr = criteria.Category.Value.ToString();
                    if (!string.Equals(ss.Category, categoryStr, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (!string.IsNullOrEmpty(criteria.State)
                        && !string.Equals(ss.State, criteria.State, StringComparison.OrdinalIgnoreCase))
                        return false;
                    return true;
                };
            }
            default:
                return (_, _) => false;
        }
    }

    private static bool CriteriaMatch(RecordType recordType, MatchCriteria a, MatchCriteria b)
    {
        return recordType switch
        {
            RecordType.TempBasal => a.Rate.HasValue && b.Rate.HasValue
                && Math.Abs(a.Rate.Value - b.Rate.Value) <= Math.Max(a.RateTolerance, b.RateTolerance),
            RecordType.SensorGlucose or RecordType.BGCheck => a.GlucoseValue.HasValue && b.GlucoseValue.HasValue
                && Math.Abs(a.GlucoseValue.Value - b.GlucoseValue.Value) <= Math.Max(a.GlucoseTolerance, b.GlucoseTolerance),
            RecordType.Bolus => a.Insulin.HasValue && b.Insulin.HasValue
                && Math.Abs(a.Insulin.Value - b.Insulin.Value) <= Math.Max(a.InsulinTolerance, b.InsulinTolerance),
            RecordType.CarbIntake or RecordType.BolusCalculation => a.Carbs.HasValue && b.Carbs.HasValue
                && Math.Abs(a.Carbs.Value - b.Carbs.Value) <= Math.Max(a.CarbsTolerance, b.CarbsTolerance),
            RecordType.DeviceEvent => string.Equals(a.EventType, b.EventType, StringComparison.OrdinalIgnoreCase),
            RecordType.Note => true,
            RecordType.StateSpan => a.Category == b.Category
                && (string.IsNullOrEmpty(a.State) || string.IsNullOrEmpty(b.State)
                    || string.Equals(a.State, b.State, StringComparison.OrdinalIgnoreCase)),
            _ => false
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
            var stateSpanCount = await _context.StateSpans.CountAsync(cancellationToken);
            var sensorGlucoseCount = await _context.SensorGlucose.CountAsync(cancellationToken);
            var bolusCount = await _context.Boluses.CountAsync(cancellationToken);
            var carbIntakeCount = await _context.CarbIntakes.CountAsync(cancellationToken);
            var bgCheckCount = await _context.BGChecks.CountAsync(cancellationToken);
            var deviceEventCount = await _context.DeviceEvents.CountAsync(cancellationToken);
            var noteCount = await _context.Notes.CountAsync(cancellationToken);
            var bolusCalcCount = await _context.BolusCalculations.CountAsync(cancellationToken);
            var tempBasalCount = await _context.TempBasals.CountAsync(cancellationToken);
            // NOTE: MeterGlucose was previously processed via DeduplicateEntriesAsync alongside
            // SensorGlucose, but there is no RecordType.MeterGlucose enum value. The old code also
            // double-processed SensorGlucose (once in Entries, once standalone). MeterGlucose dedup
            // is intentionally dropped; add a RecordType if it's needed in the future.
            var totalRecords = stateSpanCount + sensorGlucoseCount + bolusCount + carbIntakeCount
                + bgCheckCount + deviceEventCount + noteCount + bolusCalcCount + tempBasalCount;

            var processed = 0;
            var groupsCreated = 0;
            var recordsLinked = 0;
            var duplicateGroups = 0;

            // --- SensorGlucose ---
            var sensorGlucoseResult = await DeduplicateTypeAsync(
                RecordType.SensorGlucose,
                _context.SensorGlucose.OrderBy(e => e.Timestamp),
                e => new DeduplicationInput(
                    e.Id,
                    new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    e.DataSource ?? "unknown",
                    new MatchCriteria { GlucoseValue = e.Mgdl, GlucoseTolerance = 5.0 }),
                "SensorGlucose", totalRecords, processed, progress, cancellationToken);
            processed += sensorGlucoseResult.processed;
            groupsCreated += sensorGlucoseResult.groups;
            recordsLinked += sensorGlucoseResult.linked;
            duplicateGroups += sensorGlucoseResult.duplicates;

            // --- Boluses ---
            var bolusResult = await DeduplicateTypeAsync(
                RecordType.Bolus,
                _context.Boluses.OrderBy(b => b.Timestamp),
                b => new DeduplicationInput(
                    b.Id,
                    new DateTimeOffset(b.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    b.DataSource ?? "unknown",
                    new MatchCriteria { Insulin = b.Insulin, InsulinTolerance = 0.05 }),
                "Boluses", totalRecords, processed, progress, cancellationToken);
            processed += bolusResult.processed;
            groupsCreated += bolusResult.groups;
            recordsLinked += bolusResult.linked;
            duplicateGroups += bolusResult.duplicates;

            // --- CarbIntakes ---
            var carbIntakeResult = await DeduplicateTypeAsync(
                RecordType.CarbIntake,
                _context.CarbIntakes.OrderBy(c => c.Timestamp),
                c => new DeduplicationInput(
                    c.Id,
                    new DateTimeOffset(c.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    c.DataSource ?? "unknown",
                    new MatchCriteria { Carbs = c.Carbs, CarbsTolerance = 1.0 }),
                "CarbIntakes", totalRecords, processed, progress, cancellationToken);
            processed += carbIntakeResult.processed;
            groupsCreated += carbIntakeResult.groups;
            recordsLinked += carbIntakeResult.linked;
            duplicateGroups += carbIntakeResult.duplicates;

            // --- BGChecks ---
            var bgCheckResult = await DeduplicateTypeAsync(
                RecordType.BGCheck,
                _context.BGChecks.OrderBy(bg => bg.Timestamp),
                bg => new DeduplicationInput(
                    bg.Id,
                    new DateTimeOffset(bg.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    bg.DataSource ?? "unknown",
                    new MatchCriteria { GlucoseValue = bg.Glucose, GlucoseTolerance = 5.0 }),
                "BGChecks", totalRecords, processed, progress, cancellationToken);
            processed += bgCheckResult.processed;
            groupsCreated += bgCheckResult.groups;
            recordsLinked += bgCheckResult.linked;
            duplicateGroups += bgCheckResult.duplicates;

            // --- DeviceEvents ---
            var deviceEventResult = await DeduplicateTypeAsync(
                RecordType.DeviceEvent,
                _context.DeviceEvents.OrderBy(d => d.Timestamp),
                d => new DeduplicationInput(
                    d.Id,
                    new DateTimeOffset(d.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    d.DataSource ?? "unknown",
                    new MatchCriteria { EventType = d.EventType }),
                "DeviceEvents", totalRecords, processed, progress, cancellationToken);
            processed += deviceEventResult.processed;
            groupsCreated += deviceEventResult.groups;
            recordsLinked += deviceEventResult.linked;
            duplicateGroups += deviceEventResult.duplicates;

            // --- Notes ---
            var noteResult = await DeduplicateTypeAsync(
                RecordType.Note,
                _context.Notes.OrderBy(n => n.Timestamp),
                n => new DeduplicationInput(
                    n.Id,
                    new DateTimeOffset(n.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    n.DataSource ?? "unknown",
                    new MatchCriteria()),
                "Notes", totalRecords, processed, progress, cancellationToken);
            processed += noteResult.processed;
            groupsCreated += noteResult.groups;
            recordsLinked += noteResult.linked;
            duplicateGroups += noteResult.duplicates;

            // --- BolusCalculations ---
            var bolusCalcResult = await DeduplicateTypeAsync(
                RecordType.BolusCalculation,
                _context.BolusCalculations.OrderBy(bc => bc.Timestamp),
                bc => new DeduplicationInput(
                    bc.Id,
                    new DateTimeOffset(bc.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    bc.DataSource ?? "unknown",
                    new MatchCriteria { Carbs = bc.CarbInput ?? 0, CarbsTolerance = 1.0 }),
                "BolusCalculations", totalRecords, processed, progress, cancellationToken);
            processed += bolusCalcResult.processed;
            groupsCreated += bolusCalcResult.groups;
            recordsLinked += bolusCalcResult.linked;
            duplicateGroups += bolusCalcResult.duplicates;

            // --- TempBasals ---
            var tempBasalResult = await DeduplicateTypeAsync(
                RecordType.TempBasal,
                _context.TempBasals.OrderBy(t => t.StartTimestamp),
                t => new DeduplicationInput(
                    t.Id,
                    new DateTimeOffset(t.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    t.DataSource ?? "unknown",
                    new MatchCriteria { Rate = t.Rate, RateTolerance = 0.05 }),
                "TempBasals", totalRecords, processed, progress, cancellationToken);
            processed += tempBasalResult.processed;
            groupsCreated += tempBasalResult.groups;
            recordsLinked += tempBasalResult.linked;
            duplicateGroups += tempBasalResult.duplicates;

            // --- StateSpans ---
            var stateSpanResult = await DeduplicateTypeAsync(
                RecordType.StateSpan,
                _context.StateSpans.OrderBy(s => s.StartTimestamp),
                s => new DeduplicationInput(
                    s.Id,
                    new DateTimeOffset(s.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    s.Source ?? "unknown",
                    new MatchCriteria
                    {
                        Category = Enum.TryParse<StateSpanCategory>(s.Category, ignoreCase: true, out var cat)
                            ? cat : null,
                        State = s.State
                    }),
                "StateSpans", totalRecords, processed, progress, cancellationToken);
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
                EntriesProcessed = sensorGlucoseResult.processed,
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

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateTypeAsync<TEntity>(
        RecordType recordType,
        IQueryable<TEntity> query,
        Func<TEntity, DeduplicationInput> toInput,
        string phaseName,
        int totalRecords,
        int startOffset,
        IProgress<DeduplicationProgress>? progress,
        CancellationToken ct) where TEntity : class
    {
        const int batchSize = 500;
        var allEntities = await query.ToListAsync(ct);
        var inputs = allEntities.Select(toInput).ToList();

        var totalProcessed = 0;
        var totalGroups = 0;
        var totalLinked = 0;
        var totalDuplicates = 0;

        foreach (var chunk in inputs.Chunk(batchSize))
        {
            ct.ThrowIfCancellationRequested();

            var result = await DeduplicateBatchAsync(recordType, chunk, ct);
            totalProcessed += result.Processed;
            totalGroups += result.GroupsCreated;
            totalLinked += result.RecordsLinked;
            totalDuplicates += result.DuplicateGroups;

            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = startOffset + totalProcessed,
                GroupsFound = totalGroups,
                RecordsLinked = totalLinked,
                CurrentPhase = phaseName
            });
        }

        return (totalProcessed, totalGroups, totalLinked, totalDuplicates);
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
