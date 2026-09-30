using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthReadingWriter(
    IHeartRateService heartRates,
    IStepCountService stepCounts,
    IBodyWeightService bodyWeights,
    ISleepService sleep,
    NocturneDbContext db,
    ILogger<GoogleHealthReadingWriter> logger) : IGoogleHealthReadingWriter
{
    public const string Source = DataSources.GoogleHealthConnector;
    private const string SourceApp = "Google Health";
    private const int MaxReconciliationIdentifiers = 100_000;
    private readonly Dictionary<Guid, ReconciliationRun> reconciliationRuns = [];

    public Task<Guid> BeginReconciliationAsync(
        IReadOnlyCollection<string> activeTypes,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (from >= to) throw new ArgumentException("The reconciliation window must have a positive duration.");
        if (activeTypes.Except(GoogleHealthClient.SupportedTypes).Any())
            throw new ArgumentException("Unsupported reconciliation data type.", nameof(activeTypes));
        var runId = Guid.CreateVersion7();
        reconciliationRuns.Add(runId, new(db.TenantId, from.UtcDateTime, to.UtcDateTime,
            activeTypes.Distinct(StringComparer.Ordinal).ToDictionary(type => type,
                _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal)));
        return Task.FromResult(runId);
    }

    public Task StageReconciliationIdsAsync(
        Guid runId,
        string dataType,
        IReadOnlyCollection<string> identifiers,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var run = GetRun(runId);
        if (!run.Identifiers.TryGetValue(dataType, out var staged))
            throw new ArgumentException("The data type is not part of this reconciliation run.", nameof(dataType));
        foreach (var identifier in identifiers.Where(identifier => !string.IsNullOrWhiteSpace(identifier)))
        {
            if (!staged.Contains(identifier) && staged.Count >= MaxReconciliationIdentifiers)
                throw new GoogleHealthException("history_too_large", stage: "native_reconciliation_stage", dataType: dataType);
            staged.Add(identifier);
        }
        return Task.CompletedTask;
    }

    public async Task CompleteReconciliationAsync(Guid runId, CancellationToken ct)
    {
        var run = GetRun(runId);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var deletedAt = DateTime.UtcNow;
            foreach (var (type, identifiers) in run.Identifiers)
            {
                // An empty provider result is not evidence that previously imported data was deleted.
                if (identifiers.Count == 0) continue;
                switch (type)
                {
                    case "heart-rate":
                        await ReconcileRecordsAsync(db.HeartRates.Where(record => record.TenantId == run.TenantId &&
                                record.DataSource == Source && record.DeletedAt == null &&
                                record.Timestamp >= run.FromTime && record.Timestamp < run.ToTime &&
                                record.SyncIdentifier != null)
                            .Select(record => new ReconciliationRecord(record.Id, record.SyncIdentifier!)),
                            identifiers, type, batch => db.HeartRates.Where(record =>
                                    record.TenantId == run.TenantId && record.DataSource == Source && batch.Contains(record.Id))
                                .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.DeletedAt, deletedAt)
                                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct), ct);
                        break;
                    case "steps":
                        await ReconcileRecordsAsync(db.StepCounts.Where(record => record.TenantId == run.TenantId &&
                                record.DataSource == Source && record.DeletedAt == null &&
                                record.Timestamp >= run.FromTime && record.Timestamp < run.ToTime &&
                                record.SyncIdentifier != null)
                            .Select(record => new ReconciliationRecord(record.Id, record.SyncIdentifier!)),
                            identifiers, type, batch => db.StepCounts.Where(record =>
                                    record.TenantId == run.TenantId && record.DataSource == Source && batch.Contains(record.Id))
                                .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.DeletedAt, deletedAt)
                                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct), ct);
                        break;
                    case "weight":
                        var firstMills = new DateTimeOffset(DateTime.SpecifyKind(run.FromTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                        var lastMills = new DateTimeOffset(DateTime.SpecifyKind(run.ToTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                        await ReconcileRecordsAsync(db.BodyWeights.Where(record => record.TenantId == run.TenantId &&
                                record.DataSource == Source && record.DeletedAt == null &&
                                record.Mills >= firstMills && record.Mills < lastMills &&
                                record.SyncIdentifier != null)
                            .Select(record => new ReconciliationRecord(record.Id, record.SyncIdentifier!)),
                            identifiers, type, batch => db.BodyWeights.Where(record =>
                                    record.TenantId == run.TenantId && record.DataSource == Source && batch.Contains(record.Id))
                                .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.DeletedAt, deletedAt)
                                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct), ct);
                        break;
                    case "sleep":
                        await ReconcileRecordsAsync(db.SleepSessions.Where(session => session.TenantId == run.TenantId &&
                                session.Source == SleepSource.Google.ToString() && session.SourceApp == SourceApp &&
                                session.EndTime >= run.FromTime && session.EndTime < run.ToTime &&
                                session.OriginalId != null)
                            .Select(session => new ReconciliationRecord(session.Id, session.OriginalId!)),
                            identifiers, type, batch => db.SleepSessions.Where(session =>
                                    session.TenantId == run.TenantId && session.Source == SleepSource.Google.ToString() &&
                                    session.SourceApp == SourceApp && batch.Contains(session.Id)).ExecuteDeleteAsync(ct), ct);
                        break;
                }
            }
            await transaction.CommitAsync(ct);
        });
        reconciliationRuns.Remove(runId);
    }

    private static async Task ReconcileRecordsAsync(
        IQueryable<ReconciliationRecord> query, HashSet<string> identifiers, string dataType,
        Func<Guid[], Task<int>> remove, CancellationToken ct)
    {
        var records = await query.Take(MaxReconciliationIdentifiers + 1).ToListAsync(ct);
        if (records.Count > MaxReconciliationIdentifiers)
            throw new GoogleHealthException("history_too_large", stage: "native_reconciliation_complete", dataType: dataType);
        foreach (var batch in records.Where(record => !identifiers.Contains(record.Identifier))
                     .Select(record => record.Id).Chunk(500))
            await remove(batch);
    }

    public Task AbandonReconciliationAsync(Guid runId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (reconciliationRuns.TryGetValue(runId, out var run) && run.TenantId == db.TenantId)
            reconciliationRuns.Remove(runId);
        return Task.CompletedTask;
    }

    private ReconciliationRun GetRun(Guid runId) =>
        reconciliationRuns.TryGetValue(runId, out var run) && run.TenantId == db.TenantId
            ? run
            : throw new InvalidOperationException("Reconciliation run is unavailable for this tenant.");

    // Scoped to one import. A restart discards these IDs; the persisted cursor only advances
    // after successful completion, so native idempotency keys make retrying the window safe.
    private sealed record ReconciliationRun(
        Guid TenantId, DateTime FromTime, DateTime ToTime, Dictionary<string, HashSet<string>> Identifiers);

    private sealed record ReconciliationRecord(Guid Id, string Identifier);

    public async Task WriteAsync(
        IReadOnlyCollection<GoogleHealthReading> readings,
        IReadOnlyCollection<SleepSession> sleepSessions,
        int batchSize,
        CancellationToken ct)
    {
        logger.LogInformation(
            "GoogleHealthReadingWriter.WriteAsync: processing {ReadingsCount} readings and {SleepCount} sleep sessions",
            readings.Count, sleepSessions.Count);

        try
        {
            foreach (var heartRateBatch in Map(readings, "heart-rate", reading => new HeartRate
            {
                Mills = reading.Mills,
                UtcOffset = reading.UtcOffsetMinutes,
                Bpm = checked((int)reading.Value),
                Accuracy = 0,
                Device = "Google Health",
                EnteredBy = "Google Health",
                DataSource = Source,
                SyncIdentifier = GoogleHealthClient.Key(reading)
            }).Chunk(batchSize))
                await WriteBatchAsync("heart-rate", heartRateBatch.Length, heartRateBatch.Max(record => record.Mills),
                    () => heartRates.CreateHeartRatesAsync(heartRateBatch, ct));

            foreach (var stepBatch in Map(readings, "steps", reading => new StepCount
            {
                Mills = reading.Mills,
                UtcOffset = reading.UtcOffsetMinutes,
                Metric = checked((int)reading.Value),
                Source = 0,
                Device = "Google Health",
                EnteredBy = "Google Health",
                DataSource = Source,
                SyncIdentifier = GoogleHealthClient.Key(reading)
            }).Chunk(batchSize))
                await WriteBatchAsync("steps", stepBatch.Length, stepBatch.Max(record => record.Mills),
                    () => stepCounts.CreateStepCountsAsync(stepBatch, ct));

            foreach (var weightBatch in Map(readings, "weight", reading => new BodyWeight
            {
                Mills = reading.Mills,
                UtcOffset = reading.UtcOffsetMinutes,
                WeightKg = reading.Value,
                Device = "Google Health",
                EnteredBy = "Google Health",
                DataSource = Source,
                SyncIdentifier = GoogleHealthClient.Key(reading)
            }).Chunk(batchSize))
                await WriteBatchAsync("weight", weightBatch.Length, weightBatch.Max(record => record.Mills),
                    () => bodyWeights.CreateBodyWeightsAsync(weightBatch, ct));

            foreach (var session in sleepSessions)
                await WriteBatchAsync("sleep", 1,
                    new DateTimeOffset(DateTime.SpecifyKind(session.EndTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    () => sleep.UpsertSessionAsync(session, ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GoogleHealthReadingWriter.WriteAsync failed while persisting health records");
            throw;
        }
    }

    private async Task WriteBatchAsync(string dataType, int count, long latestMills, Func<Task> write)
    {
        try
        {
            await write();
        }
        catch
        {
            throw;
        }
    }

    /// <summary>
    ///     Maps one Google Health reading type to a storage record, quarantining (logging and
    ///     skipping) any single reading whose value cannot be represented — e.g. a corrupt or
    ///     out-of-range sample overflowing an int — instead of letting it fail the whole batch
    ///     and, with it, every other data type in the same tenant sync.
    /// </summary>
    private IEnumerable<TRecord> Map<TRecord>(
        IReadOnlyCollection<GoogleHealthReading> readings, string dataType, Func<GoogleHealthReading, TRecord> map)
    {
        foreach (var reading in readings.Where(reading => reading.DataType == dataType))
        {
            TRecord record;
            try
            {
                record = map(reading);
            }
            catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
            {
                logger.LogWarning(ex,
                    "Quarantining malformed Google Health {DataType} reading {SyncIdentifier}",
                    dataType, GoogleHealthClient.Key(reading));
                continue;
            }
            yield return record;
        }
    }

    public async Task PurgeAsync(CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var deletedAt = DateTime.UtcNow;
            await db.HeartRates.Where(record => record.DataSource == Source)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.DeletedAt, deletedAt)
                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), true), ct);
            await db.StepCounts.Where(record => record.DataSource == Source)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.DeletedAt, deletedAt)
                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), true), ct);
            await db.BodyWeights.Where(record => record.DataSource == Source)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.DeletedAt, deletedAt)
                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), true), ct);
            await db.SleepSessions.Where(session => session.Source == SleepSource.Google.ToString() && session.SourceApp == SourceApp)
                .ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }
}
