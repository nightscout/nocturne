using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Health;
using Microsoft.Extensions.Logging;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthReadingWriter(
    IHeartRateService heartRates,
    IStepCountService stepCounts,
    IBodyWeightService bodyWeights,
    ISleepService sleep,
    IHealthRecordTransactions records,
    ISleepSessionRepository sleepSessions,
    ITenantAccessor tenant,
    TimeProvider time,
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
        reconciliationRuns.Add(runId, new(tenant.TenantId, from.UtcDateTime, to.UtcDateTime,
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
        var run = GetRun(runId, tenant.TenantId);
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
        var run = GetRun(runId, tenant.TenantId);
        await records.CommitAsync(async (repositories, token) =>
        {
            var deletedAt = time.GetUtcNow().UtcDateTime;
            foreach (var (type, identifiers) in run.Identifiers)
            {
                // An empty provider result is not evidence that previously imported data was deleted.
                if (identifiers.Count == 0) continue;
                switch (type)
                {
                    case "heart-rate":
                        await ReconcileRecordsAsync(
                            limit => repositories.HeartRateRecords.GetSyncKeysAsync(Source, run.FromTime, run.ToTime, limit, token),
                            identifiers, type,
                            batch => repositories.HeartRateRecords.RetireAsync(Source, batch, deletedAt, token));
                        break;
                    case "steps":
                        await ReconcileRecordsAsync(
                            limit => repositories.StepCountRecords.GetSyncKeysAsync(Source, run.FromTime, run.ToTime, limit, token),
                            identifiers, type,
                            batch => repositories.StepCountRecords.RetireAsync(Source, batch, deletedAt, token));
                        break;
                    case "weight":
                        await ReconcileRecordsAsync(
                            limit => repositories.BodyWeightRecords.GetSyncKeysAsync(Source, run.FromTime, run.ToTime, limit, token),
                            identifiers, type,
                            batch => repositories.BodyWeightRecords.RetireAsync(Source, batch, deletedAt, token));
                        break;
                    case "sleep":
                        await ReconcileRecordsAsync(
                            limit => repositories.SleepSessionRecords.GetSourceAppKeysAsync(
                                SleepSource.Google, SourceApp, run.FromTime, run.ToTime, limit, token),
                            identifiers, type,
                            batch => repositories.SleepSessionRecords.RetireAsync(
                                SleepSource.Google, SourceApp, batch, deletedAt, token));
                        break;
                }
            }
        }, ct);
        reconciliationRuns.Remove(runId);
    }

    private static async Task ReconcileRecordsAsync(
        Func<int, Task<IReadOnlyList<SourceRecordKey>>> read, HashSet<string> identifiers, string dataType,
        Func<Guid[], Task<int>> remove)
    {
        var stored = await read(MaxReconciliationIdentifiers + 1);
        if (stored.Count > MaxReconciliationIdentifiers)
            throw new GoogleHealthException("history_too_large", stage: "native_reconciliation_complete", dataType: dataType);
        foreach (var batch in stored.Where(record => !identifiers.Contains(record.Key))
                     .Select(record => record.Id).Chunk(500))
            await remove(batch);
    }

    public Task AbandonReconciliationAsync(Guid runId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (reconciliationRuns.TryGetValue(runId, out var run) && run.TenantId == tenant.TenantId)
            reconciliationRuns.Remove(runId);
        return Task.CompletedTask;
    }

    private ReconciliationRun GetRun(Guid runId, Guid tenantId) =>
        reconciliationRuns.TryGetValue(runId, out var run) && run.TenantId == tenantId
            ? run
            : throw new InvalidOperationException("Reconciliation run is unavailable for this tenant.");

    // Scoped to one import. A restart discards these IDs; the persisted cursor only advances
    // after successful completion, so native idempotency keys make retrying the window safe.
    private sealed record ReconciliationRun(
        Guid TenantId, DateTime FromTime, DateTime ToTime, Dictionary<string, HashSet<string>> Identifiers);

    public async Task WriteAsync(
        IReadOnlyCollection<GoogleHealthReading> readings,
        IReadOnlyCollection<SleepSession> sleepSessions,
        int batchSize,
        CancellationToken ct)
    {
        logger.LogInformation(
            "GoogleHealthReadingWriter.WriteAsync: processing {ReadingsCount} readings and {SleepCount} sleep sessions",
            readings.Count, sleepSessions.Count);

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
            await heartRates.CreateHeartRatesAsync(heartRateBatch, ct);

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
            await stepCounts.CreateStepCountsAsync(stepBatch, ct);

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
            await bodyWeights.CreateBodyWeightsAsync(weightBatch, ct);

        foreach (var session in sleepSessions)
        {
            try
            {
                await sleep.UpsertSessionAsync(session, ct);
            }
            catch (RecreationBlockedException)
            {
                logger.LogDebug("Skipping a manually deleted Google Health sleep session");
            }
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

    public Task PurgeAsync(CancellationToken ct) =>
        records.CommitAsync(async (repositories, token) =>
        {
            // Purge releases account-independent sync keys, including prior user-deletion tombstones.
            await repositories.HeartRateRecords.PurgeSourceAsync(Source, token);
            await repositories.StepCountRecords.PurgeSourceAsync(Source, token);
            await repositories.BodyWeightRecords.PurgeSourceAsync(Source, token);
            await repositories.SleepSessionRecords.PurgeSourceAppAsync(SleepSource.Google, SourceApp, token);
        }, ct);

    public Task<SleepSessionStats?> DescribeSleepAsync(DateTime? since, DateTime last24Hours, CancellationToken ct) =>
        sleepSessions.DescribeSourceAppAsync(SleepSource.Google, SourceApp, since, last24Hours, ct);

    public async Task<int> DeleteSleepAsync(bool allowReimport, IAuditContext auditContext, CancellationToken ct)
    {
        var scope = $"data_source={Source}";
        var deleted = await sleepSessions.DeleteSourceAppAsync(SleepSource.Google, SourceApp, auditContext, scope, ct);
        if (allowReimport)
            await sleepSessions.AllowSourceAppReimportAsync(SleepSource.Google, SourceApp, auditContext, scope, ct);
        return deleted;
    }
}
