using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Oura.Configurations;
using Nocturne.Connectors.Oura.Export;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Health.Oura;

/// <summary>The records one export added or updated.</summary>
public sealed record OuraImportResult(int SleepSessions, int HeartRates, int StepDays);

/// <summary>
/// Writes an uploaded Oura data export into the current tenant, and removes what it wrote.
/// </summary>
public sealed class OuraImportService(
    IHeartRateService heartRates,
    IStepCountService stepCounts,
    ISleepService sleep,
    ISleepSessionRepository sleepSessions,
    ILogger<OuraImportService> logger) : IConnectorDataPurge
{
    private const int BatchSize = 1000;

    /// <exception cref="OuraExportException">The stream is not an Oura export.</exception>
    public async Task<OuraImportResult> ImportAsync(Stream zip, CancellationToken ct)
    {
        using var export = OuraExportArchive.Open(zip);

        var sessions = 0;
        foreach (var session in export.ReadSleepSessions())
        {
            try
            {
                await sleep.UpsertSessionAsync(session, ct);
            }
            catch (RecreationBlockedException)
            {
                logger.LogDebug("Skipping a manually deleted Oura sleep session");
                continue;
            }

            sessions++;
        }

        var readings = 0;
        foreach (var batch in export.ReadHeartRates().Chunk(BatchSize))
            readings += (await heartRates.CreateHeartRatesAsync(batch, ct)).Count();

        var days = 0;
        foreach (var batch in export.ReadDailySteps().Chunk(BatchSize))
            days += (await stepCounts.CreateStepCountsAsync(batch, ct)).Count();

        logger.LogInformation(
            "Imported an Oura export: {SleepSessions} sleep sessions, {HeartRates} heart rate readings, {StepDays} days of steps",
            sessions, readings, days);
        return new OuraImportResult(sessions, readings, days);
    }

    public string ConnectorName => OuraConnectorConfiguration.Name;

    async Task<IReadOnlyList<ConnectorRecordStats>> IConnectorDataPurge.DescribeAsync(
        DateTime? since, DateTime last24Hours, CancellationToken ct) =>
        await sleepSessions.DescribeSourceAppAsync(SleepSource.Oura, OuraExportArchive.SourceApp, since, last24Hours, ct)
            is { } stats
            ? [new(nameof(SyncDataType.Sleep), stats.Count, stats.CountLast24Hours, stats.Latest, stats.Oldest)]
            : [];

    /// <remarks>Heart rate and steps carry the data source id and go with the source purge; only sleep is left to this.</remarks>
    async Task<IReadOnlyDictionary<string, long>> IConnectorDataPurge.PurgeAsync(
        bool allowReimport, IAuditContext auditContext, CancellationToken ct)
    {
        var scope = $"data_source={DataSources.OuraConnector}";
        var deleted = await sleepSessions.DeleteSourceAppAsync(
            SleepSource.Oura, OuraExportArchive.SourceApp, auditContext, scope, ct);
        if (allowReimport)
            await sleepSessions.AllowSourceAppReimportAsync(
                SleepSource.Oura, OuraExportArchive.SourceApp, auditContext, scope, ct);
        return deleted > 0
            ? new Dictionary<string, long> { [nameof(SyncDataType.Sleep)] = deleted }
            : new Dictionary<string, long>();
    }
}
