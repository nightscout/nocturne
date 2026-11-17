using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using static Nocturne.Infrastructure.Data.Services.Deduplication.LinkedRecordTypes;

namespace Nocturne.Infrastructure.Data.Services.Deduplication;

/// <summary>
/// The tenant admin's surface of deduplication. A full run is the job-ledger job
/// <see cref="IDeduplicationAdmin.RunJob"/>, and its state lives on the tenant's
/// <c>dedup_reconcile_state</c> row, so any replica answers its status and takes its cancel, and a run
/// a restart kills resumes on whichever replica claims it next.
/// </summary>
/// <remarks>
/// <para>
/// A run proceeds in slices of <see cref="SliceLength"/>. Each slice ends at a page boundary and returns
/// where the next resumes as a <see cref="JobCursor"/>: <see cref="JobCursor.Page"/> is the index of the
/// record type in <see cref="LinkedRecordTypes.TypePhases"/>, <see cref="JobCursor.At"/> the event time
/// the type's last linked page ended on. A killed slice is run again from the cursor the last one left,
/// with the totals stored beside it.
/// </para>
/// <para>
/// The status is written by the run and, for a run that has not started, by a cancel, both holding
/// the tenant's run lock (<see cref="LockRunAsync"/>) whenever the run may not be theirs alone. A cancel
/// of a running run only stamps <see cref="DedupReconcileStateEntity.RunCancelRequestedAt"/>, which the
/// run reads after each page.
/// </para>
/// </remarks>
public sealed class DeduplicationAdmin(
    INocturneDbContextFactory contexts,
    IDuplicateLinker linker,
    DuplicateReconciler reconciler,
    ILogger<DeduplicationAdmin> logger,
    ITenantAccessor? tenantAccessor = null,
    ICategoryReadContext? categoryReadContext = null) : IDeduplicationAdmin
{
    /// <summary>How long one slice of a run lasts before it hands back to the scheduler.</summary>
    internal TimeSpan SliceLength { get; init; } = TimeSpan.FromMinutes(5);

    private const int RunLockClass = 0x4E44_5255;

    /// <inheritdoc />
    public async Task<Guid> StartRunAsync(CancellationToken cancellationToken = default)
    {
        // Without a tenant the job's scope would read no rows and report a vacuous success.
        if (tenantAccessor?.Context is not { } tenant)
            throw new InvalidOperationException(
                "Cannot start a deduplication run without a resolved tenant.");

        // A run reads the tenant's whole history to decide which links are orphaned and which records
        // match, and the scheduler's scope is not clamped.
        if (TenantReader.Of(tenant, categoryReadContext ?? throw new InvalidOperationException(
                "Cannot start a deduplication run without the caller's read context.")).HistoryClamped)
        {
            throw new UnauthorizedAccessException(
                "A deduplication run reads the tenant's whole history, which a history-limited caller cannot.");
        }

        await using var db = await contexts.CreateAsync(cancellationToken);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await LockRunAsync(db, token);
            var state = await DuplicateReconciler.TrackedStateAsync(db, token);
            if (Read(state.Run) is { Status.State: DeduplicationJobState.Pending or DeduplicationJobState.Running } inFlight)
                return inFlight.Status.JobId;

            var run = new StoredRun(
                new DeduplicationJobStatus
                {
                    JobId = Guid.CreateVersion7(),
                    State = DeduplicationJobState.Pending,
                    StartedAt = DateTime.UtcNow,
                },
                RunTotals.None,
                []);
            state.Run = Write(run);
            state.RunCancelRequestedAt = null;
            await db.SaveChangesAsync(token);

            if (db.Database.IsNpgsql())
                await JobLedgerRepository.RequestInTransactionAsync(db, IDeduplicationAdmin.RunJob, token);
            return run.Status.JobId;
        }, ct: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DeduplicationJobStatus?> GetRunStatusAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateAsync(cancellationToken);
        var run = Read(await RunJsonAsync(db, cancellationToken));
        return run?.Status.JobId == runId ? run.Status : null;
    }

    /// <inheritdoc />
    public async Task<bool> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateAsync(cancellationToken);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await LockRunAsync(db, token);
            var run = Read(await RunJsonAsync(db, token));
            if (run is null || run.Status.JobId != runId)
                return false;

            switch (run.Status.State)
            {
                case DeduplicationJobState.Pending:
                    await WriteRunAsync(db, run with
                    {
                        Status = run.Status with { State = DeduplicationJobState.Cancelled, CompletedAt = DateTime.UtcNow },
                    }, token);
                    return true;
                case DeduplicationJobState.Running:
                    var at = DateTime.UtcNow;
                    await db.DedupReconcileState
                        .Where(s => s.TenantId == db.TenantId)
                        .ExecuteUpdateAsync(s => s.SetProperty(e => e.RunCancelRequestedAt, at), token);
                    return true;
                default:
                    return false;
            }
        }, ct: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<JobCursor?> RunAsync(JobCursor? from, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        await using var db = await contexts.CreateAsync(ct);
        if (await BeginAsync(db, from, ct) is not { } begun)
            return null;
        var (run, resumeAt) = begun;

        var slice = Stopwatch.StartNew();
        var totals = run.Totals;
        try
        {
            if (resumeAt is null)
            {
                var orphans = await DeleteOrphanedLinksAsync(db, ct);
                if (orphans > 0)
                    logger.LogInformation("Deleted {Orphans} orphaned links before deduplicating", orphans);

                var total = 0;
                foreach (var phase in TypePhases)
                    total += await phase.CountAsync(db, ct);
                totals = totals with { TotalRecords = total };
            }

            var after = resumeAt?.At?.UtcDateTime;
            for (var index = resumeAt?.Page ?? 0; index < TypePhases.Length; index++, after = null)
            {
                var phase = TypePhases[index];
                await foreach (var page in phase.Pages(db, after, ct))
                {
                    var linked = await linker.DeduplicateBatchAsync(phase.RecordType, page.Inputs, ct);
                    totals = totals.Add(phase.RecordType, linked);
                    after = page.End;

                    if (await db.DedupReconcileState.AnyAsync(
                            s => s.TenantId == db.TenantId && s.RunCancelRequestedAt != null, ct))
                    {
                        await EndAsync(db, run, totals, DeduplicationJobState.Cancelled, null);
                        logger.LogInformation("Deduplication run {RunId} cancelled", run.Status.JobId);
                        return null;
                    }

                    run = run with { Status = run.Status with { Progress = totals.Progress(phase.Name) }, Totals = totals };
                    if (slice.Elapsed >= SliceLength)
                    {
                        var resume = new JobCursor(At: new DateTimeOffset(DateTime.SpecifyKind(page.End, DateTimeKind.Utc)), Page: index);
                        await WriteRunAsync(db, run with
                        {
                            Checkpoints = [.. run.Checkpoints.Where(c => c.At == resumeAt), new Checkpoint(resume, totals)],
                        }, ct);
                        return resume;
                    }

                    await WriteRunAsync(db, run, ct);
                }

                // Records linked by an earlier run are skipped, so a run over existing history creates
                // no links and on its own would heal nothing. Merging the type's groups repairs history
                // ingested while the connectors' clocks drifted apart. Only wide-eligible types can hold
                // such a split, and the merge loads every primary of the type, which for a dense stream
                // like glucose would be hundreds of thousands of rows for nothing.
                if (DuplicateMatching.WideMatchableTypes.Contains(phase.RecordType))
                    totals = totals with { Duplicates = totals.Duplicates + await reconciler.MergeAllAsync(phase.RecordType, ct) };
            }

            await EndAsync(db, run, totals, DeduplicationJobState.Completed, null);
            logger.LogInformation(
                "Deduplication run {RunId} completed: {TotalRecords} records processed, {Groups} groups created, {Linked} records linked, {Duplicates} duplicate groups",
                run.Status.JobId, totals.Processed, totals.Groups, totals.Linked, totals.Duplicates);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Deduplication run {RunId} failed", run.Status.JobId);
            await EndAsync(db, run, totals, DeduplicationJobState.Failed, ex.Message);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<LinkedRecord>> GetLinkedRecordsAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateAsync(cancellationToken);
        var entities = await db.LinkedRecords
            .Where(lr => lr.CanonicalId == canonicalId)
            .OrderBy(static lr => lr.SourceTimestamp)
            .ToListAsync(cancellationToken);

        return entities
            .Select(e => (Entity: e, Type: ParseRecordType(e.RecordType, logger)))
            .Where(static x => x.Type.HasValue)
            .Select(x => ToModel(x.Entity, x.Type!.Value));
    }

    /// <inheritdoc />
    public async Task<LinkedRecord?> GetLinkedRecordAsync(
        RecordType recordType,
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateAsync(cancellationToken);
        var recordTypeStr = RecordTypeKeys.Key(recordType);

        var entity = await db.LinkedRecords
            .FirstOrDefaultAsync(lr =>
                lr.RecordType == recordTypeStr && lr.RecordId == recordId,
                cancellationToken);

        return entity is null ? null : ToModel(entity, recordType);
    }

    private static LinkedRecord ToModel(LinkedRecordEntity entity, RecordType recordType) => new()
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

    /// <summary>
    /// Marks the tenant's pending or running run as running and returns it with where it resumes, its
    /// totals those at that place; null when there is none, as for a run cancelled before it started.
    /// The run resumes from <paramref name="from"/> only when one of its own slices ended there: the
    /// ledger keeps the cursor of an earlier run whose last slice died before the ledger recorded its
    /// end, and hands it to the next claim whether that claim starts the new run or follows a first
    /// slice that recorded nothing. Any other cursor starts the run from the beginning.
    /// </summary>
    private static Task<(StoredRun Run, JobCursor? ResumeAt)?> BeginAsync(
        NocturneDbContext db, JobCursor? from, CancellationToken ct) =>
        db.ExecuteInTransactionAsync<(StoredRun, JobCursor?)?>(async token =>
        {
            await LockRunAsync(db, token);
            if (Read(await RunJsonAsync(db, token)) is not { Status.State: DeduplicationJobState.Pending or DeduplicationJobState.Running } run)
                return null;

            var checkpoint = from is null ? null : run.Checkpoints.LastOrDefault(c => c.At == from);
            var started = run with
            {
                Status = run.Status with { State = DeduplicationJobState.Running },
                Totals = checkpoint?.Totals ?? RunTotals.None,
            };
            await WriteRunAsync(db, started, token);
            return (started, checkpoint?.At);
        }, ct: ct);

    /// <summary>
    /// Stores the run as ended in <paramref name="state"/>, whatever the run's own token says: the
    /// outcome has to reach the admin polling it.
    /// </summary>
    private static async Task EndAsync(
        NocturneDbContext db, StoredRun run, RunTotals totals, DeduplicationJobState state, string? error)
    {
        var completedAt = DateTime.UtcNow;
        var duration = completedAt - run.Status.StartedAt;
        var result = state switch
        {
            DeduplicationJobState.Completed => totals.Result(duration),
            DeduplicationJobState.Failed => new DeduplicationResult { Duration = duration, Success = false, ErrorMessage = error },
            _ => null,
        };

        await WriteRunAsync(db, run with
        {
            Status = run.Status with { State = state, CompletedAt = completedAt, Result = result },
            Totals = totals,
        }, CancellationToken.None);
        await db.DedupReconcileState
            .Where(s => s.TenantId == db.TenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.RunCancelRequestedAt, (DateTime?)null), CancellationToken.None);
    }

    /// <summary>
    /// Takes the tenant's run lock for the transaction: a start, a cancel of a run not yet started and
    /// a run's start serialise on it, so none of them overwrites a status another has just written.
    /// </summary>
    private static Task LockRunAsync(NocturneDbContext db, CancellationToken ct) =>
        db.LockForTransactionAsync(RunLockClass, db.TenantId, ct);

    private static Task<string?> RunJsonAsync(NocturneDbContext db, CancellationToken ct) =>
        db.DedupReconcileState.AsNoTracking()
            .Where(s => s.TenantId == db.TenantId)
            .Select(s => s.Run)
            .SingleOrDefaultAsync(ct);

    private static Task WriteRunAsync(NocturneDbContext db, StoredRun run, CancellationToken ct)
    {
        var json = Write(run);
        return db.DedupReconcileState
            .Where(s => s.TenantId == db.TenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Run, json), ct);
    }

    private static StoredRun? Read(string? json) => json is null ? null : JsonSerializer.Deserialize<StoredRun>(json);

    private static string Write(StoredRun run) => JsonSerializer.Serialize(run);

    /// <summary>
    /// What <c>dedup_reconcile_state.run</c> holds: the status served to the admin, the totals so far,
    /// and the totals where the last slice started and where it ended. The next slice resumes from the
    /// one at its cursor: the end, or the start again when the last slice died before the job ledger
    /// recorded where it ended, however many times in a row that happens.
    /// </summary>
    internal sealed record StoredRun(DeduplicationJobStatus Status, RunTotals Totals, IReadOnlyList<Checkpoint> Checkpoints);

    /// <summary>The totals of a run at <paramref name="At"/>, where a slice ended.</summary>
    internal sealed record Checkpoint(JobCursor At, RunTotals Totals);

    /// <summary>A run's counts so far.</summary>
    internal sealed record RunTotals(
        int TotalRecords,
        int Processed,
        int Groups,
        int Linked,
        int Duplicates,
        IReadOnlyDictionary<RecordType, int> ProcessedByType)
    {
        public static RunTotals None { get; } = new(0, 0, 0, 0, 0, new Dictionary<RecordType, int>());

        public RunTotals Add(RecordType recordType, DeduplicationBatchResult batch) => this with
        {
            Processed = Processed + batch.Processed,
            Groups = Groups + batch.GroupsCreated,
            Linked = Linked + batch.RecordsLinked,
            Duplicates = Duplicates + batch.DuplicateGroups,
            ProcessedByType = new Dictionary<RecordType, int>(ProcessedByType)
            {
                [recordType] = ProcessedByType.GetValueOrDefault(recordType) + batch.Processed,
            },
        };

        public DeduplicationProgress Progress(string phase) => new()
        {
            TotalRecords = TotalRecords,
            ProcessedRecords = Processed,
            GroupsFound = Groups,
            RecordsLinked = Linked,
            CurrentPhase = phase,
        };

        public DeduplicationResult Result(TimeSpan duration) => new()
        {
            TotalRecordsProcessed = Processed,
            CanonicalGroupsCreated = Groups,
            RecordsLinked = Linked,
            DuplicateGroupsFound = Duplicates,
            Duration = duration,
            StateSpansProcessed = ProcessedByType.GetValueOrDefault(RecordType.StateSpan),
            SensorGlucoseProcessed = ProcessedByType.GetValueOrDefault(RecordType.SensorGlucose),
            BolusesProcessed = ProcessedByType.GetValueOrDefault(RecordType.Bolus),
            CarbIntakesProcessed = ProcessedByType.GetValueOrDefault(RecordType.CarbIntake),
            BGChecksProcessed = ProcessedByType.GetValueOrDefault(RecordType.BGCheck),
            DeviceEventsProcessed = ProcessedByType.GetValueOrDefault(RecordType.DeviceEvent),
            NotesProcessed = ProcessedByType.GetValueOrDefault(RecordType.Note),
            BolusCalculationsProcessed = ProcessedByType.GetValueOrDefault(RecordType.BolusCalculation),
            TempBasalsProcessed = ProcessedByType.GetValueOrDefault(RecordType.TempBasal),
            Success = true,
        };
    }
}
