using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     The Glooko XT read path: one Socket.IO session per sync reads the record stream a week at
///     a time and the CSV export a fortnight at a time over the window the caller resolved, then
///     <see cref="Map"/> fans everything out into the types the tenant has enabled. Publishing
///     stays with the connector service, which owns the publisher and the sync result.
/// </summary>
public class GlookoXtSyncPath(IGlookoXtDataClient dataClient, ILogger logger)
{
    private readonly GlookoXtRecordMapper _recordMapper = new(logger);
    private readonly GlookoXtExportMapper _exportMapper = new(logger);

    /// <summary>
    ///     The device catalogue is the same for every patient and changes when Glooko adds a
    ///     product, so one copy per process, refreshed daily, serves every tenant's sync.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (DateTime FetchedAt, IReadOnlyDictionary<long, string> Names)> ProductCatalogue = new();
    private static readonly TimeSpan ProductCatalogueLifetime = TimeSpan.FromHours(24);
    private static readonly string[] ProductTypes = ["pump", "cgm", "bgm"];

    /// <summary>The export types: what only the CSV carries. A run that asks for none of them skips the export.</summary>
    private static readonly SyncDataType[] ExportTypes = [SyncDataType.StateSpans, SyncDataType.Profiles, SyncDataType.DeviceEvents];

    public sealed record Fetched(
        List<GlookoXtRecord> Records,
        IReadOnlyDictionary<long, string> ProductNames,
        List<GlookoXtExport> Exports,
        GlookoXtAccount? Account);

    /// <summary>
    ///     Reads everything the window needs. Throws <see cref="GlookoXtAuthenticationException"/>
    ///     when the server refuses the token; any other failure propagates as-is.
    /// </summary>
    public async Task<Fetched> FetchAsync(
        string token,
        DateTime fromUtc,
        DateTime toUtc,
        HashSet<SyncDataType> activeTypes,
        Func<DateTime, DateTime, Task>? reportWindow,
        CancellationToken ct)
    {
        var all = new List<GlookoXtRecord>();

        await using var session = await dataClient.ConnectAsync(GlookoXtConstants.ServerUrl, token, ct);

        var productNames = await ProductNamesAsync(session, ct);
        var account = await AccountAsync(session, ct);

        foreach (var (chunkFrom, chunkTo) in DateChunker.Chunk(fromUtc, toUtc, GlookoXtConstants.FetchChunk))
        {
            ct.ThrowIfCancellationRequested();
            if (reportWindow is not null) await reportWindow(chunkFrom, chunkTo);
            all.AddRange(await session.GetCollectedDataAsync(chunkFrom, chunkTo, ct));
        }

        // A window boundary can hand the same record back twice; the server id is its identity.
        var records = all
            .GroupBy(r => r.Id ?? long.MinValue)
            .SelectMany(g => g.Key == long.MinValue ? g : g.Take(1))
            .ToList();

        var exports = new List<GlookoXtExport>();
        if (ExportTypes.Any(activeTypes.Contains))
        {
            // The export is asked for by calendar day in the account's zone; a day of slack on each
            // side covers the zone offset the window's UTC bounds do not know about.
            var firstDay = DateOnly.FromDateTime(fromUtc.AddDays(-1));
            var lastDay = DateOnly.FromDateTime(toUtc.AddDays(1));
            for (var day = firstDay; day <= lastDay; day = day.AddDays(GlookoXtConstants.ExportChunk.Days))
            {
                ct.ThrowIfCancellationRequested();
                var chunkEnd = day.AddDays(GlookoXtConstants.ExportChunk.Days - 1);
                if (chunkEnd > lastDay) chunkEnd = lastDay;
                exports.Add(await session.ExportRecordsAsync(day, chunkEnd, ct));
            }
        }

        return new Fetched(records, productNames, exports, account);
    }

    /// <summary>
    ///     Fans the fetch out into Nocturne records, reading glucose in the unit the account states
    ///     and falling back to inference from the values when the profile could not be read.
    /// </summary>
    public GlookoXtMappedBatch Map(Fetched fetched, string? unitSetting = null)
    {
        var effective = GlookoXtGlucoseUnits.Effective(unitSetting ?? GlookoXtConstants.GlucoseUnits.Auto, fetched.Account?.Unit);
        if (fetched.Account?.Unit is null)
            logger.LogDebug("Glooko XT profile did not state a glucose unit; inferring from the readings");

        var batch = _recordMapper.Map(fetched.Records, effective, fetched.ProductNames);
        _exportMapper.Map(fetched.Exports, effective, batch);
        return batch;
    }

    private async Task<IReadOnlyDictionary<long, string>> ProductNamesAsync(IGlookoXtSession session, CancellationToken ct)
    {
        if (ProductCatalogue.TryGetValue(GlookoXtConstants.ServerUrl, out var cached) && DateTime.UtcNow - cached.FetchedAt < ProductCatalogueLifetime)
            return cached.Names;

        var names = new Dictionary<long, string>();
        try
        {
            foreach (var type in ProductTypes)
            foreach (var product in await session.GetProductsAsync(type, ct))
            {
                if (product.Id is { } id && !string.IsNullOrWhiteSpace(product.Name))
                    names[id] = product.Name;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the Glooko XT device catalogue; records keep their sync source as device");
            return cached.Names ?? new Dictionary<long, string>();
        }

        ProductCatalogue[GlookoXtConstants.ServerUrl] = (DateTime.UtcNow, names);
        return names;
    }

    private async Task<GlookoXtAccount?> AccountAsync(IGlookoXtSession session, CancellationToken ct)
    {
        try
        {
            return await session.GetAccountAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the Glooko XT account settings");
            return null;
        }
    }
}
