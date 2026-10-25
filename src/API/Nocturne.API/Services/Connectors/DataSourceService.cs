using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Demo;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Domain service for querying and managing the data sources (connectors and direct Nightscout uploads)
/// connected to a Nocturne tenant. Aggregates connector metadata, last-seen timestamps from entries
/// and treatments, and enabled/disabled state for display in the admin UI.
/// </summary>
/// <seealso cref="IDataSourceService"/>
public class DataSourceService : IDataSourceService
{
    /// <summary>
    /// How far back discovery counts records. Published on every <see cref="DataSourceInfo"/> as its
    /// <see cref="DataSourceInfo.TotalEntriesWindowDays"/>.
    /// </summary>
    internal const int DiscoveryWindowDays = 30;

    private readonly INocturneDbContextFactory _contexts;
    private readonly ISensorGlucoseRepository _sensorGlucose;
    private readonly IMeterGlucoseRepository _meterGlucose;
    private readonly ICalibrationRepository _calibrations;
    private readonly IAuditContext _auditContext;
    private readonly IConnectorConfigurationService _connectorConfiguration;
    private readonly ProfileActivationWriter _profileActivationWriter;
    private readonly IEnumerable<IConnectorPurgeGuard> _purgeGuards;
    private readonly IEnumerable<IConnectorDataPurge> _dataPurges;
    private readonly ILogger<DataSourceService> _logger;

    public DataSourceService(
        INocturneDbContextFactory contexts,
        ISensorGlucoseRepository sensorGlucose,
        IMeterGlucoseRepository meterGlucose,
        ICalibrationRepository calibrations,
        IAuditContext auditContext,
        IConnectorConfigurationService connectorConfiguration,
        ProfileActivationWriter profileActivationWriter,
        IEnumerable<IConnectorPurgeGuard> purgeGuards,
        IEnumerable<IConnectorDataPurge> dataPurges,
        ILogger<DataSourceService> logger
    )
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _profileActivationWriter = profileActivationWriter;
        _sensorGlucose = sensorGlucose;
        _meterGlucose = meterGlucose;
        _calibrations = calibrations;
        _auditContext = auditContext;
        _connectorConfiguration = connectorConfiguration;
        _purgeGuards = purgeGuards;
        _dataPurges = dataPurges;
        _logger = logger;
    }

    /// <remarks>
    /// <paramref name="Handle"/> is which handle the bucket's key names, or <see langword="null"/>
    /// when no contributing table could tell.
    /// </remarks>
    private record TableStats(long Count, int CountLast24H, DateTime Latest, DateTime? Oldest, SourceHandle? Handle);

    private record SourceBucket(string? Key, TableStats Stats);

    /// <summary>
    /// What a source owns in one table, in the ways the service reads it: the buckets discovery
    /// groups it into (none for a table discovery does not list), a count by source for the summary,
    /// the profile names the purge must refresh, and the delete and re-import release of the purge.
    /// </summary>
    private interface IOwnedTable
    {
        SyncDataType Type { get; }

        Task<List<SourceBucket>> BucketsAsync(
            NocturneDbContext db, DateTime windowStart, DateTime last24HoursDate, CancellationToken ct);

        Task<long> CountAsync(NocturneDbContext db, string source, CancellationToken ct);

        Task<List<string>> ProfileNamesAsync(NocturneDbContext db, string source, CancellationToken ct);

        Task<int> SoftDeleteAsync(
            NocturneDbContext db, string source, IAuditContext audit, string scope, CancellationToken ct);

        Task AllowReimportAsync(
            NocturneDbContext db, string source, IAuditContext audit, string scope, CancellationToken ct);
    }

    private sealed class OwnedTable<T>(
        SyncDataType type,
        Func<NocturneDbContext, IQueryable<T>> rows,
        Func<string, Expression<Func<T, bool>>> ownedBy,
        Func<IQueryable<T>, DateTime, DateTime, CancellationToken, Task<List<SourceBucket>>>? buckets = null,
        Expression<Func<T, string>>? profileName = null)
        : IOwnedTable where T : class, ITenantScoped, ISoftDeletable
    {
        public SyncDataType Type => type;

        public Task<List<SourceBucket>> BucketsAsync(
            NocturneDbContext db, DateTime windowStart, DateTime last24HoursDate, CancellationToken ct) =>
            buckets is null
                ? Task.FromResult(new List<SourceBucket>())
                : buckets(rows(db), windowStart, last24HoursDate, ct);

        public Task<long> CountAsync(NocturneDbContext db, string source, CancellationToken ct) =>
            rows(db).Where(ownedBy(source)).LongCountAsync(ct);

        public Task<List<string>> ProfileNamesAsync(NocturneDbContext db, string source, CancellationToken ct) =>
            profileName is null
                ? Task.FromResult(new List<string>())
                : rows(db).Where(ownedBy(source)).Select(profileName).Distinct().ToListAsync(ct);

        public Task<int> SoftDeleteAsync(
            NocturneDbContext db, string source, IAuditContext audit, string scope, CancellationToken ct) =>
            db.AuditedSoftDeleteAsync(rows(db).Where(ownedBy(source)), audit, scope, ct);

        public Task AllowReimportAsync(
            NocturneDbContext db, string source, IAuditContext audit, string scope, CancellationToken ct) =>
            db.AuditedAllowReimportAsync(rows(db).Where(ownedBy(source)), audit, scope, ct);
    }

    private static OwnedTable<T> TimeSeries<T>(SyncDataType type, Func<NocturneDbContext, IQueryable<T>> rows)
        where T : class, ITenantScoped, ISoftDeletable, ISourcedEntity, IObservationTimestamped =>
        new(type, rows, SourceFilter.For<T>, TimeSeriesBuckets);

    private static readonly IOwnedTable[] OwnedTables =
    [
        TimeSeries<BolusEntity>(SyncDataType.Boluses, db => db.Boluses),
        TimeSeries<CarbIntakeEntity>(SyncDataType.CarbIntake, db => db.CarbIntakes),
        TimeSeries<BolusCalculationEntity>(SyncDataType.BolusCalculations, db => db.BolusCalculations),
        TimeSeries<DeviceEventEntity>(SyncDataType.DeviceEvents, db => db.DeviceEvents),
        TimeSeries<BGCheckEntity>(SyncDataType.BGChecks, db => db.BGChecks),
        TimeSeries<NoteEntity>(SyncDataType.Notes, db => db.Notes),
        TimeSeries<ApsSnapshotEntity>(SyncDataType.DeviceStatus, db => db.ApsSnapshots),
        TimeSeries<TempBasalEntity>(SyncDataType.TempBasals, db => db.TempBasals),
        new OwnedTable<StateSpanEntity>(
            SyncDataType.StateSpans, db => db.StateSpans, source => s => s.Source == source, StateSpanBuckets),
        new OwnedTable<ProfileActivationEntity>(
            SyncDataType.StateSpans, db => db.ProfileActivations, SourceFilter.For<ProfileActivationEntity>),
        TimeSeries<HeartRateEntity>(SyncDataType.HeartRate, db => db.HeartRates),
        TimeSeries<StepCountEntity>(SyncDataType.Steps, db => db.StepCounts),
        new OwnedTable<BodyWeightEntity>(
            SyncDataType.BodyWeight, db => db.BodyWeights, SourceFilter.For<BodyWeightEntity>, BodyWeightBuckets),
        new OwnedTable<TherapySettingsEntity>(
            SyncDataType.Profiles, db => db.TherapySettings, SourceFilter.For<TherapySettingsEntity>,
            profileName: e => e.ProfileName),
        new OwnedTable<BasalScheduleEntity>(
            SyncDataType.Profiles, db => db.BasalSchedules, SourceFilter.For<BasalScheduleEntity>,
            profileName: e => e.ProfileName),
        new OwnedTable<CarbRatioScheduleEntity>(
            SyncDataType.Profiles, db => db.CarbRatioSchedules, SourceFilter.For<CarbRatioScheduleEntity>,
            profileName: e => e.ProfileName),
        new OwnedTable<SensitivityScheduleEntity>(
            SyncDataType.Profiles, db => db.SensitivitySchedules, SourceFilter.For<SensitivityScheduleEntity>,
            profileName: e => e.ProfileName),
        new OwnedTable<TargetRangeScheduleEntity>(
            SyncDataType.Profiles, db => db.TargetRangeSchedules, SourceFilter.For<TargetRangeScheduleEntity>,
            profileName: e => e.ProfileName),
    ];

    private static SourceHandle HandleOf(bool fromDataSource) =>
        fromDataSource ? SourceHandle.DataSource : SourceHandle.Device;

    private static async Task<List<SourceBucket>> TimeSeriesBuckets<T>(
        IQueryable<T> rows, DateTime windowStart, DateTime last24HoursDate, CancellationToken ct)
        where T : class, ISourcedEntity, IObservationTimestamped
    {
        var stats = await rows
            .Where(e => e.Timestamp >= windowStart)
            .GroupBy(e => e.DataSource ?? e.Device)
            .Select(g => new
            {
                Key = g.Key,
                FromDataSource = g.Max(x => x.DataSource) != null,
                Count = g.LongCount(),
                Count24H = g.Count(x => x.Timestamp >= last24HoursDate),
                Latest = g.Max(x => x.Timestamp),
                Oldest = (DateTime?)g.Min(x => x.Timestamp),
            })
            .ToListAsync(ct);

        return stats
            .Select(s => new SourceBucket(
                s.Key, new TableStats(s.Count, s.Count24H, s.Latest, s.Oldest, HandleOf(s.FromDataSource))))
            .ToList();
    }

    private static async Task<List<SourceBucket>> BodyWeightBuckets(
        IQueryable<BodyWeightEntity> rows, DateTime windowStart, DateTime last24HoursDate, CancellationToken ct)
    {
        var fromMills = new DateTimeOffset(windowStart, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var last24HoursMills = new DateTimeOffset(last24HoursDate, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var stats = await rows
            .Where(e => e.Mills >= fromMills)
            .GroupBy(e => e.DataSource ?? e.Device)
            .Select(g => new
            {
                Key = g.Key,
                FromDataSource = g.Max(x => x.DataSource) != null,
                Count = g.LongCount(),
                Count24H = g.Count(x => x.Mills >= last24HoursMills),
                Latest = g.Max(x => x.Mills),
                Oldest = g.Min(x => x.Mills),
            })
            .ToListAsync(ct);

        static DateTime Utc(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
        return stats
            .Select(s => new SourceBucket(
                s.Key,
                new TableStats(s.Count, s.Count24H, Utc(s.Latest), Utc(s.Oldest), HandleOf(s.FromDataSource))))
            .ToList();
    }

    // StateSpan records one undifferentiated origin: its writers populate Source from the
    // reported device string (DeviceStatusDecomposer) or from the row's data source, falling back
    // to the entering party (TreatmentDecomposer). It therefore names neither handle in
    // particular and contributes no evidence about which one its bucket's key is.
    private static async Task<List<SourceBucket>> StateSpanBuckets(
        IQueryable<StateSpanEntity> rows, DateTime windowStart, DateTime last24HoursDate, CancellationToken ct)
    {
        var stats = await rows
            .Where(s => s.StartTimestamp >= windowStart)
            .GroupBy(s => s.Source)
            .Select(g => new
            {
                Key = g.Key,
                Count = g.LongCount(),
                Count24H = g.Count(x => x.StartTimestamp >= last24HoursDate),
                Latest = g.Max(x => x.StartTimestamp),
                Oldest = (DateTime?)g.Min(x => x.StartTimestamp),
            })
            .ToListAsync(ct);

        return stats
            .Select(s => new SourceBucket(s.Key, new TableStats(s.Count, s.Count24H, s.Latest, s.Oldest, null)))
            .ToList();
    }

    private static void ApplyStatus(DataSourceInfo info, DateTimeOffset now, int activeMinutes, int staleMinutes)
    {
        var minutesSinceLast = info.LastSeen.HasValue
            ? (int)(now - info.LastSeen.Value).TotalMinutes
            : int.MaxValue;
        info.MinutesSinceLastData = minutesSinceLast;
        info.Status = minutesSinceLast switch
        {
            _ when minutesSinceLast < activeMinutes => "active",
            _ when minutesSinceLast < staleMinutes => "stale",
            _ => "inactive",
        };
    }

    private (int activeMinutes, int staleMinutes) ResolveThresholds(
        string? connectorDataSourceId,
        Dictionary<string, (int active, int stale)>? configOverrides)
    {
        const int defaultActive = 15;
        const int defaultStale = 60;

        if (connectorDataSourceId == null)
            return (defaultActive, defaultStale);

        if (configOverrides != null &&
            configOverrides.TryGetValue(connectorDataSourceId, out var overrides))
        {
            var active = overrides.active > 0 ? overrides.active : 0;
            var stale = overrides.stale > 0 ? overrides.stale : 0;

            if (active > 0 || stale > 0)
            {
                var meta = ConnectorMetadataService.GetByDataSourceId(connectorDataSourceId);
                return (
                    active > 0 ? active : meta?.DefaultActiveThresholdMinutes ?? defaultActive,
                    stale > 0 ? stale : meta?.DefaultStaleThresholdMinutes ?? defaultStale);
            }
        }

        var connectorMeta = ConnectorMetadataService.GetByDataSourceId(connectorDataSourceId);
        if (connectorMeta != null)
            return (connectorMeta.DefaultActiveThresholdMinutes, connectorMeta.DefaultStaleThresholdMinutes);

        return (defaultActive, defaultStale);
    }

    private async Task<Dictionary<string, (int active, int stale)>> LoadThresholdOverridesAsync(
        NocturneDbContext db,
        CancellationToken ct)
    {
        var configs = await db.ConnectorConfigurations
            .AsNoTracking()
            .Select(c => new { c.ConnectorName, c.ConfigurationJson })
            .ToListAsync(ct);

        var result = new Dictionary<string, (int active, int stale)>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in configs)
        {
            var meta = ConnectorMetadataService.GetByConnectorId(config.ConnectorName);
            if (meta == null || string.IsNullOrEmpty(meta.DataSourceId)) continue;

            try
            {
                using var doc = JsonDocument.Parse(config.ConfigurationJson);
                var root = doc.RootElement;

                var active = root.TryGetProperty("activeThresholdMinutes", out var aProp) && aProp.TryGetInt32(out var a) ? a : 0;
                var stale = root.TryGetProperty("staleThresholdMinutes", out var sProp) && sProp.TryGetInt32(out var s) ? s : 0;

                result[meta.DataSourceId] = (active, stale);
            }
            catch
            {
                // Invalid JSON — skip
            }
        }

        return result;
    }

    /// <summary>
    /// Aggregates every non-glucose table a source can produce rows in, bucketed by the source
    /// identifier the row carries. The bucket key is <c>DataSource ?? Device</c>, the same key the
    /// discovery merge resolves an entry under, and each bucket records which handle produced it
    /// (<see cref="SourceHandle"/>) so an entry surfaced from a bucket alone can say so — or records
    /// that no contributing table could tell.
    /// </summary>
    private async Task<Dictionary<string, TableStats>> GetNonGlucoseStatsAsync(
        NocturneDbContext db,
        DateTime windowStart, DateTime last24HoursDate, CancellationToken ct)
    {
        var result = new Dictionary<string, TableStats>(StringComparer.OrdinalIgnoreCase);

        // A table that stores the two handles in separate columns can say which one the key came
        // from; one that stores a single undifferentiated origin cannot, and contributes null so the
        // bucket keeps whatever evidence the other tables carry. Only a row bearing an actual
        // DataSource column value proves the key is a data source, so that answer wins over Device.
        static SourceHandle? CombineHandles(SourceHandle? left, SourceHandle? right) =>
            left == SourceHandle.DataSource || right == SourceHandle.DataSource
                ? SourceHandle.DataSource
                : left ?? right;

        void Merge(string? key, TableStats add)
        {
            if (string.IsNullOrEmpty(key) || add.Count == 0) return;
            if (result.TryGetValue(key, out var existing))
            {
                result[key] = new TableStats(
                    existing.Count + add.Count,
                    existing.CountLast24H + add.CountLast24H,
                    add.Latest > existing.Latest ? add.Latest : existing.Latest,
                    add.Oldest.HasValue && (!existing.Oldest.HasValue || add.Oldest.Value < existing.Oldest.Value)
                        ? add.Oldest : existing.Oldest,
                    CombineHandles(existing.Handle, add.Handle));
            }
            else
            {
                result[key] = add;
            }
        }

        foreach (var bucket in await TimeSeriesBuckets(db.MeterGlucose, windowStart, last24HoursDate, ct))
            Merge(bucket.Key, bucket.Stats);

        foreach (var table in OwnedTables)
        foreach (var bucket in await table.BucketsAsync(db, windowStart, last24HoursDate, ct))
            Merge(bucket.Key, bucket.Stats);

        foreach (var purge in _dataPurges)
        {
            if (ConnectorMetadataService.GetByConnectorId(purge.ConnectorName) is not { } owner) continue;

            foreach (var stats in await purge.DescribeAsync(windowStart, last24HoursDate, ct))
                Merge(owner.DataSourceId, new TableStats(
                    stats.Count, stats.CountLast24Hours, stats.Latest, stats.Oldest, SourceHandle.DataSource));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<List<DataSourceInfo>> GetActiveDataSourcesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        _logger.LogDebug("Getting active data sources");

        var now = DateTimeOffset.UtcNow;
        var last24Hours = now.AddHours(-24);
        var windowStart = now.AddDays(-DiscoveryWindowDays).UtcDateTime;
        var last24HoursDate = last24Hours.UtcDateTime;

        // Get distinct devices from V4 sensor glucose inside the discovery window
        var entryDevices = await db
            .SensorGlucose.Where(e => e.Timestamp >= windowStart && e.Device != null && e.Device != "")
            .GroupBy(e => e.Device)
            .Select(g => new
            {
                Device = g.Key!,
                DataSource = g.Max(e => e.DataSource),
                LastTimestamp = g.Max(e => e.Timestamp),
                FirstTimestamp = g.Min(e => e.Timestamp),
                TotalCount = g.LongCount(),
                Last24HCount = g.Count(e => e.Timestamp >= last24HoursDate),
            })
            // Sibling devices sharing a data source both resolve to its single non-glucose bucket,
            // and only the first of them claims it. Ordering by the group key makes which one that is
            // the same on every refresh instead of whatever the grouping happened to return.
            .OrderBy(d => d.Device)
            .ToListAsync(cancellationToken);

        // Also check APS snapshots for devices that might not have entries. Discovery keys on Device,
        // not DataSource: an entry's identity here is the uploader string that CreateDataSourceInfo
        // parses for name, category and icon, so a connector-imported snapshot still lists its rig.
        // The connector keeps its own entry via GetNonGlucoseStatsAsync, which keys on DataSource.
        var deviceStatusDevices = await db
            .ApsSnapshots.Where(ds =>
                ds.Timestamp >= windowStart && ds.Device != null && ds.Device != ""
            )
            .GroupBy(ds => ds.Device)
            .Select(g => new
            {
                Device = g.Key!,
                DataSource = g.Max(ds => ds.DataSource),
                LastMills = g.Max(ds => ds.Timestamp).ToMills(),
            })
            .ToListAsync(cancellationToken);

        // Batch-aggregate non-glucose tables
        var nonGlucoseStats = await GetNonGlucoseStatsAsync(db, windowStart, last24HoursDate, cancellationToken);

        // Load user threshold overrides and connector configs for LastSuccessfulSync
        var thresholdOverrides = await LoadThresholdOverridesAsync(db, cancellationToken);
        var connectorConfigs = await db.ConnectorConfigurations
            .AsNoTracking()
            .Select(c => new { c.ConnectorName, c.LastSuccessfulSync })
            .ToListAsync(cancellationToken);

        var dataSources = new List<DataSourceInfo>();
        var processedNonGlucoseKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A bucket is consumed by exactly one entry, or by none and surfaced as its own entry below.
        // An entry claims the bucket its own key names first; the Device-field fallback may then take
        // only a bucket no entry claims that way, which is the same entry the leftover loop would
        // have built for it. Without that exclusivity a bucket reachable from two entries is added
        // to both and any consumer summing the list double-counts it.
        var primaryKeys = new HashSet<string>(
            entryDevices.Select(d => d.DataSource ?? d.Device), StringComparer.OrdinalIgnoreCase);

        TableStats? Claim(string key) =>
            nonGlucoseStats.TryGetValue(key, out var stats) && processedNonGlucoseKeys.Add(key)
                ? stats
                : null;

        static void MergeStats(DataSourceInfo info, TableStats stats)
        {
            info.TotalEntries += stats.Count;
            info.EntriesLast24Hours += stats.CountLast24H;

            var latest = new DateTimeOffset(stats.Latest, TimeSpan.Zero);
            if (latest > info.LastSeen)
                info.LastSeen = latest;

            if (stats.Oldest.HasValue)
            {
                var oldest = new DateTimeOffset(stats.Oldest.Value, TimeSpan.Zero);
                if (!info.FirstSeen.HasValue || oldest < info.FirstSeen)
                    info.FirstSeen = oldest;
            }
        }

        void ApplyConnectorAndStatus(DataSourceInfo info, string key)
        {
            var connectorMeta = ConnectorMetadataService.GetByDataSourceId(key);
            if (connectorMeta != null)
            {
                info.ConnectorId = connectorMeta.ConnectorId;
                var connConfig = connectorConfigs.FirstOrDefault(c =>
                    c.ConnectorName.Equals(connectorMeta.ConnectorName, StringComparison.OrdinalIgnoreCase));
                if (connConfig?.LastSuccessfulSync != null)
                    info.LastSuccessfulSync = new DateTimeOffset(connConfig.LastSuccessfulSync.Value, TimeSpan.Zero);
            }

            var (activeMinutes, staleMinutes) = ResolveThresholds(key, thresholdOverrides);
            ApplyStatus(info, now, activeMinutes, staleMinutes);
        }

        foreach (var device in entryDevices)
        {
            var info = CreateDataSourceInfo(device.Device, device.DataSource, SourceHandle.Device);
            info.LastSeen = new DateTimeOffset(device.LastTimestamp, TimeSpan.Zero);
            info.FirstSeen = new DateTimeOffset(device.FirstTimestamp, TimeSpan.Zero);
            info.TotalEntries = device.TotalCount;
            info.EntriesLast24Hours = device.Last24HCount;

            // Check if there's device status data
            var dsDevice = deviceStatusDevices.FirstOrDefault(d => d.Device == device.Device);
            var lastSeenMills = device.LastTimestamp.ToMills();
            if (dsDevice != null && dsDevice.LastMills > lastSeenMills)
            {
                info.LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(dsDevice.LastMills);
            }

            // Merge non-glucose stats
            var mergeKey = device.DataSource ?? device.Device;
            if (Claim(mergeKey) is { } ngStats)
                MergeStats(info, ngStats);

            // Also try matching by Device field
            if (mergeKey != device.Device
                && !primaryKeys.Contains(device.Device)
                && Claim(device.Device) is { } ngDeviceStats)
                MergeStats(info, ngDeviceStats);

            ApplyConnectorAndStatus(info, mergeKey);

            dataSources.Add(info);
        }

        // Add any device status only devices
        foreach (var dsDevice in deviceStatusDevices)
        {
            if (!dataSources.Any(d => d.DeviceId == dsDevice.Device))
            {
                var info = CreateDataSourceInfo(dsDevice.Device, dsDevice.DataSource, SourceHandle.Device);
                info.LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(dsDevice.LastMills);
                info.FirstSeen = info.LastSeen;
                info.TotalEntries = 0;
                info.EntriesLast24Hours = 0;

                // Merge non-glucose stats if available
                if (Claim(dsDevice.Device) is { } ngStats)
                    MergeStats(info, ngStats);

                ApplyConnectorAndStatus(info, dsDevice.DataSource ?? dsDevice.Device);

                dataSources.Add(info);
            }
        }

        // Add data sources found only in non-glucose tables
        foreach (var (key, stats) in nonGlucoseStats)
        {
            if (processedNonGlucoseKeys.Contains(key)) continue;

            var info = CreateDataSourceInfo(key, key, stats.Handle ?? SourceHandle.Unknown);
            info.LastSeen = new DateTimeOffset(stats.Latest, TimeSpan.Zero);
            info.FirstSeen = stats.Oldest.HasValue ? new DateTimeOffset(stats.Oldest.Value, TimeSpan.Zero) : info.LastSeen;
            info.TotalEntries = stats.Count;
            info.EntriesLast24Hours = stats.CountLast24H;

            ApplyConnectorAndStatus(info, key);

            dataSources.Add(info);
        }

        return dataSources.OrderByDescending(d => d.LastSeen).ToList();
    }

    /// <inheritdoc />
    public async Task<DataSourceInfo?> GetDataSourceInfoAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        var sources = await GetActiveDataSourcesAsync(cancellationToken);
        return sources.FirstOrDefault(s => s.DeviceId == deviceId || s.Id == deviceId);
    }

    /// <inheritdoc />
    public async Task<List<AvailableConnector>> GetAvailableConnectorsAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var connectors = ConnectorMetadataService.GetAll()
            .Select(connector => new AvailableConnector
            {
                Id = connector.ConnectorId,
                Name = connector.DisplayName,
                Category = connector.Category.ToString().ToLowerInvariant(),
                Description = connector.Description,
                Icon = connector.Icon,
                Available = true,
                RequiresServerConfig = true,
                DataSourceId = connector.DataSourceId,
                DocumentationUrl = GetConnectorDocumentationUrl(connector.ConnectorName),
                ConfigFields = null,
            })
            .OrderBy(connector => connector.Name)
            .ToList();

        // Check which connectors have actual saved configuration in the database
        var configuredNames = await db.ConnectorConfigurations
            .AsNoTracking()
            .Select(c => c.ConnectorName.ToLower())
            .ToListAsync(cancellationToken);

        var configuredSet = new HashSet<string>(configuredNames, StringComparer.OrdinalIgnoreCase);

        foreach (var connector in connectors)
        {
            connector.IsConfigured = configuredSet.Contains(connector.Id ?? "");
        }

        return connectors;
    }

    private static string? GetConnectorDocumentationUrl(string connectorName)
    {
        return connectorName.ToLowerInvariant() switch
        {
            "dexcom" => UrlConstants.External.DocsDexcom,
            "librelinkup" => UrlConstants.External.DocsLibre,
            "glooko" => UrlConstants.External.DocsGlooko,
            _ => null,
        };
    }

    /// <inheritdoc />
    public ConnectorCapabilities? GetConnectorCapabilities(string connectorId)
    {
        if (string.IsNullOrWhiteSpace(connectorId))
        {
            return null;
        }

        var registration = ConnectorMetadataService.GetRegistrationByConnectorId(connectorId);
        if (registration == null)
        {
            return null;
        }

        return new ConnectorCapabilities
        {
            SupportedDataTypes = registration.SupportedDataTypes
                ?.Select(type => type.ToString())
                .Distinct()
                .ToList()
                ?? new List<string>(),
            SupportsHistoricalSync = registration.SupportsHistoricalSync,
            MaxHistoricalDays = registration.MaxHistoricalDays > 0
                ? registration.MaxHistoricalDays
                : null,
            SupportsManualSync = registration.SupportsManualSync
        };
    }

    /// <inheritdoc />
    public List<UploaderApp> GetUploaderApps()
    {
        return new List<UploaderApp>
        {
            new()
            {
                Id = "xdrip",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.Cgm,
                Icon = "xdrip",
                Url = "https://github.com/NightscoutFoundation/xDrip",
            },
            new()
            {
                Id = "spike",
                Platform = UploaderPlatform.iOS,
                Category = UploaderCategory.Cgm,
                Icon = "spike",
                Url = "https://spike-app.com",
            },
            new()
            {
                Id = "loop",
                Platform = UploaderPlatform.iOS,
                Category = UploaderCategory.AidSystem,
                Icon = "loop",
                Url = "https://loopkit.github.io/loopdocs/",
            },
            new()
            {
                Id = "aaps",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.AidSystem,
                Icon = "aaps",
                Url = "https://wiki.aaps.app",
            },
            new()
            {
                Id = "trio",
                Platform = UploaderPlatform.iOS,
                Category = UploaderCategory.AidSystem,
                Icon = "trio",
                Url = "https://triodocs.org",
            },
            new()
            {
                Id = "iaps",
                Platform = UploaderPlatform.iOS,
                Category = UploaderCategory.AidSystem,
                Icon = "iaps",
                Url = "https://iaps.readthedocs.io",
            },
            new()
            {
                Id = "nightscout-uploader",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.Uploader,
                Icon = "nightscout",
                Url = "https://github.com/nightscout/android-uploader",
            },
            new()
            {
                Id = "xdrip4ios",
                Platform = UploaderPlatform.iOS,
                Category = UploaderCategory.Cgm,
                Icon = "xdrip4ios",
                Url = "https://github.com/JohanDegraworksve/xdripswift",
            },
            new()
            {
                Id = "juggluco",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.Cgm,
                Icon = "juggluco",
                Url = "https://juggluco.nl",
            },
            new()
            {
                Id = "glucotracker",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.Cgm,
                Icon = "glucotracker",
                Url = "https://glucotracker.app",
            },
            new()
            {
                Id = "prelude",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.Uploader,
                Icon = "prelude",
            },
        };
    }

    /// <inheritdoc />
    public async Task<ServicesOverview> GetServicesOverviewAsync(
        string baseUrl,
        bool isAuthenticated,
        CancellationToken cancellationToken = default
    )
    {
        var dataSources = await GetActiveDataSourcesAsync(cancellationToken);

        return new ServicesOverview
        {
            ActiveDataSources = dataSources,
            AvailableConnectors = await GetAvailableConnectorsAsync(cancellationToken),
            UploaderApps = GetUploaderApps(),
            ApiEndpoint = new ApiEndpointInfo
            {
                BaseUrl = baseUrl,
                RequiresApiSecret = true,
                IsAuthenticated = isAuthenticated,
                EntriesEndpoint = "/api/v1/entries",
                TreatmentsEndpoint = "/api/v1/treatments",
                DeviceStatusEndpoint = "/api/v1/devicestatus",
            },
        };
    }

    /// <summary>
    /// Create a DataSourceInfo from a device identifier
    /// </summary>
    private DataSourceInfo CreateDataSourceInfo(string deviceId, string? dataSource, SourceHandle handle)
    {
        var info = new DataSourceInfo
        {
            Id = GenerateId(deviceId),
            DeviceId = deviceId,
            DeviceIdHandle = handle,
            TotalEntriesWindowDays = DiscoveryWindowDays,
        };

        // Parse device identifier to determine type
        var lowerDevice = deviceId.ToLowerInvariant();

        // Detect source type and category
        if (lowerDevice.Contains("xdrip4ios") || lowerDevice.Contains("xdripswift"))
        {
            info.Name = "xDrip4iOS";
            info.SourceType = "xdrip4ios";
            info.Category = "cgm";
            info.Icon = "xdrip4ios";
            info.Description = ExtractDeviceDescription(deviceId, "xDrip4iOS on");
        }
        else if (lowerDevice.Contains("xdrip"))
        {
            info.Name = "xDrip+";
            info.SourceType = "xdrip";
            info.Category = "cgm";
            info.Icon = "xdrip";
            info.Description = ExtractDeviceDescription(deviceId, "xDrip+ on");
        }
        else if (lowerDevice.Contains("juggluco"))
        {
            info.Name = "Juggluco";
            info.SourceType = "juggluco";
            info.Category = "cgm";
            info.Icon = "juggluco";
            info.Description = ExtractDeviceDescription(deviceId, "Juggluco on");
        }
        else if (lowerDevice.Contains("glucotracker"))
        {
            info.Name = "GlucoTracker";
            info.SourceType = "glucotracker";
            info.Category = "cgm";
            info.Icon = "glucotracker";
            info.Description = ExtractDeviceDescription(deviceId, "GlucoTracker on");
        }
        else if (lowerDevice.Contains("spike"))
        {
            info.Name = "Spike";
            info.SourceType = "spike";
            info.Category = "cgm";
            info.Icon = "spike";
            info.Description = ExtractDeviceDescription(deviceId, "Spike");
        }
        else if (lowerDevice.Contains("loop") && !lowerDevice.Contains("openaps"))
        {
            info.Name = "Loop";
            info.SourceType = "loop";
            info.Category = "aid-system";
            info.Icon = "loop";
            info.Description = "Loop iOS AID System";
        }
        else if (lowerDevice.Contains("aaps") || lowerDevice.Contains("androidaps"))
        {
            info.Name = "AndroidAPS";
            info.SourceType = "aaps";
            info.Category = "aid-system";
            info.Icon = "aaps";
            info.Description = "AndroidAPS AID System";
        }
        else if (lowerDevice.Contains("openaps") || lowerDevice.Contains("oref"))
        {
            info.Name = "OpenAPS";
            info.SourceType = "openaps";
            info.Category = "aid-system";
            info.Icon = "openaps";
            info.Description = "OpenAPS AID System";
        }
        else if (lowerDevice.Contains("trio"))
        {
            info.Name = "Trio";
            info.SourceType = "trio";
            info.Category = "aid-system";
            info.Icon = "trio";
            info.Description = "Trio iOS AID System";
        }
        else if (lowerDevice.Contains("iaps"))
        {
            info.Name = "iAPS";
            info.SourceType = "iaps";
            info.Category = "aid-system";
            info.Icon = "iaps";
            info.Description = "iAPS iOS AID System";
        }
        else if (lowerDevice.Contains("dexcom"))
        {
            info.Name = "Dexcom";
            info.SourceType = "dexcom";
            info.Category = "cgm";
            info.Icon = "dexcom";
            info.Description = ExtractDeviceDescription(deviceId, "Dexcom CGM");
        }
        else if (lowerDevice.Contains("libre") || lowerDevice.Contains("freestyle"))
        {
            info.Name = "FreeStyle Libre";
            info.SourceType = "libre";
            info.Category = "cgm";
            info.Icon = "libre";
            info.Description = "FreeStyle Libre CGM";
        }
        else if (
            (lowerDevice.Contains("medtronic")
                || lowerDevice.Contains("minimed")
                || lowerDevice.Contains("carelink"))
            && ConnectorMetadataService.GetByDataSourceId(dataSource) != null
        )
        {
            info.Name = "Medtronic";
            info.SourceType = "medtronic";
            info.Category = "pump";
            info.Icon = "medtronic";
            info.Description = "Medtronic Pump/CGM";
        }
        else if (lowerDevice.Contains("omnipod"))
        {
            info.Name = "Omnipod";
            info.SourceType = "omnipod";
            info.Category = "pump";
            info.Icon = "omnipod";
            info.Description = "Omnipod Pump";
        }
        else if (lowerDevice.Contains("tandem") || lowerDevice.Contains("t:slim"))
        {
            info.Name = "Tandem";
            info.SourceType = "tandem";
            info.Category = "pump";
            info.Icon = "tandem";
            info.Description = "Tandem Pump";
        }
        // Check if this is data from a connector using centralized metadata
        else if (ConnectorMetadataService.GetByDataSourceId(dataSource) is { } connectorInfo)
        {
            info.Name = connectorInfo.ConnectorName;
            info.SourceType = connectorInfo.DataSourceId;
            info.Category = "connector";
            info.Icon = connectorInfo.Icon;
            info.Description = connectorInfo.Description;
        }
        else if (dataSource == DataSources.DemoService)
        {
            info.Name = "Demo Data";
            info.SourceType = "demo";
            info.Category = "demo";
            info.Icon = "demo";
            info.Description = "Simulated demo data";
        }
        else
        {
            // Unknown device - use the raw identifier
            info.Name = CleanDeviceName(deviceId);
            info.SourceType = "unknown";
            info.Category = "unknown";
            info.Icon = "device";
            info.Description = deviceId;
        }

        return info;
    }


    /// <inheritdoc />
    public async Task<ConnectorDataSummary> GetConnectorDataSummaryAsync(
        string connectorId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        _logger.LogDebug("Getting data summary for connector: {ConnectorId}", connectorId);

        // Resolve the connector metadata to find the correct data source ID
        var metadata = ConnectorMetadataService.GetByConnectorId(connectorId);
        if (metadata == null)
        {
            return new ConnectorDataSummary { ConnectorId = connectorId };
        }

        var deviceId = metadata.DataSourceId;
        var counts = new Dictionary<string, long>();

        // Glucose
        var sensorGlucoseCount = await db
            .SensorGlucose.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (sensorGlucoseCount > 0) counts[nameof(SyncDataType.Glucose)] = sensorGlucoseCount;

        var meterGlucoseCount = await db
            .MeterGlucose.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (meterGlucoseCount > 0) counts[nameof(SyncDataType.ManualBG)] = meterGlucoseCount;

        var calibrationsCount = await db
            .Calibrations.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (calibrationsCount > 0) counts[nameof(SyncDataType.Calibrations)] = calibrationsCount;

        foreach (var table in OwnedTables)
        {
            var count = await table.CountAsync(db, deviceId, cancellationToken);
            if (count > 0) counts[table.Type.ToString()] = counts.GetValueOrDefault(table.Type.ToString()) + count;
        }

        foreach (var purge in PurgesOf(metadata))
        foreach (var stats in await purge.DescribeAsync(null, DateTime.UtcNow.AddHours(-24), cancellationToken))
            counts[stats.DataType] = counts.GetValueOrDefault(stats.DataType) + stats.Count;

        return new ConnectorDataSummary
        {
            ConnectorId = connectorId,
            RecordCounts = counts,
        };
    }

    /// <inheritdoc />
    public async Task<DataSourceDeleteResult> DeleteConnectorDataAsync(
        string connectorId,
        bool allowReimport = false,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        _logger.LogInformation("Deleting data for connector: {ConnectorId}", connectorId);

        try
        {
            // Resolve the connector metadata to find the correct data source ID
            var metadata = ConnectorMetadataService.GetByConnectorId(connectorId);
            if (metadata == null)
            {
                return DataSourceDeleteResult.Failed(
                    connectorId,
                    DataSourceDeleteError.NotFound,
                    $"Connector not found: {connectorId}");
            }

            _logger.LogInformation(
                "Resolved connector {ConnectorId} to device ID {DeviceId}",
                connectorId,
                metadata.DataSourceId
            );

            return await PurgeConnectorDataAsync(db, metadata, connectorId, allowReimport, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting data for connector: {ConnectorId}", connectorId);
            return DataSourceDeleteResult.Failed(
                connectorId,
                DataSourceDeleteError.DeleteFailed,
                "Failed to delete connector data");
        }
    }

    /// <summary>
    /// The one purge of a connector's data: holds its syncs off (its purge guard, else the persisted
    /// active flag), deletes everything attributable to its data source, then what it wrote to tables
    /// the source filter cannot reach (<see cref="IConnectorDataPurge"/>).
    /// </summary>
    private async Task<DataSourceDeleteResult> PurgeConnectorDataAsync(
        NocturneDbContext db,
        ConnectorMetadataService.ConnectorDisplayInfo metadata,
        string configurationName,
        bool allowReimport,
        CancellationToken cancellationToken
    )
    {
        var deviceId = metadata.DataSourceId;
        Dictionary<string, long> deletedCounts;
        var guard = _purgeGuards.FirstOrDefault(g =>
            g.ConnectorName.Equals(metadata.ConnectorName, StringComparison.OrdinalIgnoreCase));
        if (guard is not null)
        {
            await using var paused = await guard.PauseSyncsAsync(cancellationToken);
            if (paused is null)
            {
                return DataSourceDeleteResult.Failed(
                    configurationName,
                    DataSourceDeleteError.SyncRunning,
                    $"A sync of connector {configurationName} is running");
            }

            deletedCounts = await DeleteConnectorSourceDataAsync(db, metadata, allowReimport, cancellationToken);
        }
        else
        {
            var reEnable = allowReimport
                && (await _connectorConfiguration.GetConfigurationAsync(configurationName, cancellationToken))?.IsActive == true;

            // Disable the connector before purging so a scheduled or in-flight sync cannot
            // re-import the data we are about to delete.
            await _connectorConfiguration.SetActiveAsync(
                configurationName, isActive: false, _auditContext.SubjectName, cancellationToken);

            deletedCounts = await DeleteConnectorSourceDataAsync(db, metadata, allowReimport, cancellationToken);

            // With nothing of the source left stored, the connector's next sync imports its
            // history from the start.
            if (reEnable)
                await _connectorConfiguration.SetActiveAsync(
                    configurationName, isActive: true, _auditContext.SubjectName, cancellationToken);
        }

        _logger.LogInformation(
            "Deleted data for connector {ConnectorId} (device {DeviceId}): {DeletedCounts}",
            configurationName,
            deviceId,
            string.Join(", ", deletedCounts.Select(kv => $"{kv.Value} {kv.Key}"))
        );

        return new DataSourceDeleteResult
        {
            Success = true,
            DataSource = deviceId,
            DeletedCounts = deletedCounts,
        };
    }

    private async Task<Dictionary<string, long>> DeleteConnectorSourceDataAsync(
        NocturneDbContext db,
        ConnectorMetadataService.ConnectorDisplayInfo metadata,
        bool allowReimport,
        CancellationToken cancellationToken
    )
    {
        var deletedCounts = await DeleteAllSourceDataAsync(db, metadata.DataSourceId, allowReimport, cancellationToken);
        foreach (var purge in PurgesOf(metadata))
        {
            foreach (var (type, count) in await purge.PurgeAsync(allowReimport, _auditContext, cancellationToken))
                deletedCounts[type] = deletedCounts.GetValueOrDefault(type) + count;
        }

        return deletedCounts;
    }

    private IEnumerable<IConnectorDataPurge> PurgesOf(ConnectorMetadataService.ConnectorDisplayInfo metadata) =>
        _dataPurges.Where(p => p.ConnectorName.Equals(metadata.ConnectorName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Deletes every data type attributable to <paramref name="source"/> (<see cref="SourceFilter"/>),
    /// routing each entity through the strongest audited path it supports. Returns the per-type
    /// deleted counts (non-zero only).
    /// </summary>
    /// <remarks>
    /// Capability matrix (entity interfaces decide the path):
    /// <list type="bullet">
    /// <item>Glucose and the auditable+soft-deletable records (Bolus, CarbIntake, BolusCalculation,
    ///   DeviceEvent, BGCheck, Note, ApsSnapshot, TempBasal, StateSpan, ProfileActivation,
    ///   TherapySettings and the four schedules) take the audited soft-delete path,
    ///   which stamps the <c>deleted_by_user</c> flag the soft-delete dedup reads to block re-import
    ///   (<see cref="SoftDeleteDedupExtensions"/>).</item>
    /// <item>Heart rate, step counts and body weights are soft-deletable but not auditable: the same
    ///   path soft-deletes and stamps them and records the bulk summary. Their create path reads the
    ///   stamp on a row holding the record's sync key or primary key, so it blocks their re-import the
    ///   same way; once released, a record keyed by its primary key revives its row in place.</item>
    /// </list>
    /// </remarks>
    private async Task<Dictionary<string, long>> DeleteAllSourceDataAsync(
        NocturneDbContext db,
        string source,
        bool allowReimport,
        CancellationToken cancellationToken
    )
    {
        // Glucose: audited soft-delete, user-attributed (via the V4 repositories).
        var sensorGlucoseDeleted = await _sensorGlucose.DeleteBySourceAsync(source, WriteOrigin.Live, cancellationToken);
        var meterGlucoseDeleted = await _meterGlucose.DeleteBySourceAsync(source, WriteOrigin.Live, cancellationToken);
        var calibrationsDeleted = await _calibrations.DeleteBySourceAsync(source, WriteOrigin.Live, cancellationToken);

        var scope = $"data_source={source}";
        var deletedCounts = new Dictionary<string, long>();
        void Add(SyncDataType type, long deleted)
        {
            if (deleted > 0) deletedCounts[type.ToString()] = deletedCounts.GetValueOrDefault(type.ToString()) + deleted;
        }

        Add(SyncDataType.Glucose, sensorGlucoseDeleted);
        Add(SyncDataType.ManualBG, meterGlucoseDeleted);
        Add(SyncDataType.Calibrations, calibrationsDeleted);
        var purgedProfiles = new HashSet<string>();
        foreach (var table in OwnedTables)
        {
            purgedProfiles.UnionWith(await table.ProfileNamesAsync(db, source, cancellationToken));
            Add(table.Type, await table.SoftDeleteAsync(db, source, _auditContext, scope, cancellationToken));
        }
        await _profileActivationWriter.AfterBulkRemovalAsync(purgedProfiles, cancellationToken);

        if (allowReimport)
            await AllowSourceReimportAsync(db, source, scope, cancellationToken);

        return deletedCounts;
    }

    /// <summary>
    /// Releases every user tombstone <paramref name="source"/> produced, including those of earlier
    /// deletes, so the source can send its records again.
    /// </summary>
    private async Task AllowSourceReimportAsync(NocturneDbContext db, string source, string scope, CancellationToken ct)
    {
        await db.AuditedAllowReimportAsync(db.SensorGlucose.FromSource(source), _auditContext, scope, ct);
        await db.AuditedAllowReimportAsync(db.MeterGlucose.FromSource(source), _auditContext, scope, ct);
        await db.AuditedAllowReimportAsync(db.Calibrations.FromSource(source), _auditContext, scope, ct);
        foreach (var table in OwnedTables)
            await table.AllowReimportAsync(db, source, _auditContext, scope, ct);
    }

    internal static string GenerateId(string deviceId) => $"ds-{HashUtils.Sha256Hex(deviceId)[..8]}";

    internal static string CleanDeviceName(string deviceId)
    {
        var name = deviceId.Replace("-", " ").Replace("_", " ").Trim();

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
    }

    private static string ExtractDeviceDescription(string deviceId, string prefix)
    {
        var parts = deviceId.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            return $"{prefix} ({string.Join(" ", parts.Skip(1))})";
        }
        return prefix;
    }

    /// <inheritdoc />
    public async Task<DataSourceDeleteResult> DeleteDataSourceDataAsync(
        string dataSourceId,
        bool allowReimport = false,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        _logger.LogInformation("Deleting data for data source: {DataSourceId}", dataSourceId);

        try
        {
            var owner = ConnectorMetadataService.GetByDataSourceId(dataSourceId);
            if (owner is not null)
                return await PurgeConnectorDataAsync(db, owner, owner.ConnectorName, allowReimport, cancellationToken);

            var sources = await GetActiveDataSourcesAsync(cancellationToken);
            var source = sources.FirstOrDefault(s =>
                s.Id == dataSourceId || s.DeviceId == dataSourceId
            );

            if (source == null)
            {
                _logger.LogWarning("Data source not found: {DataSourceId}", dataSourceId);
                return DataSourceDeleteResult.Failed(
                    dataSourceId,
                    DataSourceDeleteError.NotFound,
                    $"Data source not found: {dataSourceId}");
            }

            var deviceId = source.DeviceId;

            var connector = ConnectorMetadataService.GetByDataSourceId(deviceId);
            if (connector is not null)
                return await PurgeConnectorDataAsync(db, connector, connector.ConnectorName, allowReimport, cancellationToken);

            var deletedCounts = await DeleteAllSourceDataAsync(db, deviceId, allowReimport, cancellationToken);

            _logger.LogInformation(
                "Deleted data for {DeviceId}: {DeletedCounts}",
                deviceId,
                string.Join(", ", deletedCounts.Select(kv => $"{kv.Value} {kv.Key}"))
            );

            return new DataSourceDeleteResult
            {
                Success = true,
                DataSource = deviceId,
                DeletedCounts = deletedCounts,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error deleting data for data source: {DataSourceId}",
                dataSourceId
            );
            return DataSourceDeleteResult.Failed(
                dataSourceId,
                DataSourceDeleteError.DeleteFailed,
                "Failed to delete data source data");
        }
    }

    /// <inheritdoc />
    public async Task<DataSourceDeleteResult> DeleteDemoDataAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        _logger.LogInformation("Deleting all demo data");

        try
        {
            var treatmentsDeleted = await DemoDataPurge.PurgeTreatmentsAsync(db, cancellationToken);
            var glucoseDeleted = await DemoDataPurge.PurgeEntriesAsync(db, cancellationToken);
            var deviceStatusDeleted = await DemoDataPurge.PurgeDeviceStatusAsync(db, cancellationToken);

            var deletedCounts = new Dictionary<string, long>();
            if (glucoseDeleted > 0) deletedCounts[nameof(SyncDataType.Glucose)] = glucoseDeleted;
            if (treatmentsDeleted > 0) deletedCounts["Treatments"] = treatmentsDeleted;
            if (deviceStatusDeleted > 0) deletedCounts[nameof(SyncDataType.DeviceStatus)] = deviceStatusDeleted;

            _logger.LogInformation(
                "Deleted demo data: {DeletedCounts}",
                string.Join(", ", deletedCounts.Select(kv => $"{kv.Value} {kv.Key}"))
            );

            return new DataSourceDeleteResult
            {
                Success = true,
                DataSource = DataSources.DemoService,
                DeletedCounts = deletedCounts,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting demo data");
            return DataSourceDeleteResult.Failed(
                DataSources.DemoService,
                DataSourceDeleteError.DeleteFailed,
                "Failed to delete demo data");
        }
    }

    /// <inheritdoc />
    public async Task<DataSourceStats> GetDataSourceStatsAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var oneDayAgoDate = now.AddHours(-24).UtcDateTime;

        // Query V4 sensor glucose stats
        var sgStats = await db
            .SensorGlucose.FromSource(dataSource)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.LongCount(),
                Last24H = g.Count(sg => sg.Timestamp >= oneDayAgoDate),
                Latest = g.Max(sg => (DateTime?)sg.Timestamp),
                Oldest = g.Min(sg => (DateTime?)sg.Timestamp),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Query state span stats
        var stateSpanStats = await db
            .StateSpans.Where(s => s.Source == dataSource)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalStateSpans = g.LongCount(),
                StateSpansLast24Hours = g.Count(s => s.StartTimestamp >= oneDayAgoDate),
                LastStateSpanTime = g.Max(s => (DateTime?)s.StartTimestamp),
                FirstStateSpanTime = g.Min(s => (DateTime?)s.StartTimestamp),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Query V4 table counts for per-type breakdown
        var meterGlucoseTotal = await db
            .MeterGlucose.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var meterGlucose24h = meterGlucoseTotal > 0
            ? await db.MeterGlucose
                .FromSource(dataSource)
                .Where(mg => mg.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var bolusesTotal = await db
            .Boluses.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var boluses24h = bolusesTotal > 0
            ? await db.Boluses
                .FromSource(dataSource)
                .Where(b => b.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var carbIntakesTotal = await db
            .CarbIntakes.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var carbIntakes24h = carbIntakesTotal > 0
            ? await db.CarbIntakes
                .FromSource(dataSource)
                .Where(c => c.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var bolusCalcsTotal = await db
            .BolusCalculations.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var bolusCalcs24h = bolusCalcsTotal > 0
            ? await db.BolusCalculations
                .FromSource(dataSource)
                .Where(bc => bc.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var notesTotal = await db
            .Notes.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var notes24h = notesTotal > 0
            ? await db.Notes
                .FromSource(dataSource)
                .Where(n => n.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var deviceEventsTotal = await db
            .DeviceEvents.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var deviceEvents24h = deviceEventsTotal > 0
            ? await db.DeviceEvents
                .FromSource(dataSource)
                .Where(de => de.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var deviceStatusTotal = await db
            .ApsSnapshots.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var deviceStatus24h = deviceStatusTotal > 0
            ? await db.ApsSnapshots
                .FromSource(dataSource)
                .Where(ds => ds.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var bgChecksTotal = await db
            .BGChecks.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var bgChecks24h = bgChecksTotal > 0
            ? await db.BGChecks
                .FromSource(dataSource)
                .Where(b => b.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var tempBasalsTotal = await db
            .TempBasals.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var tempBasals24h = tempBasalsTotal > 0
            ? await db.TempBasals
                .FromSource(dataSource)
                .Where(t => t.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        // Build per-type breakdown dictionaries
        var typeBreakdown = new Dictionary<string, long>();
        var typeBreakdown24h = new Dictionary<string, int>();

        var glucoseTotal = sgStats?.Total ?? 0;
        var glucose24h = sgStats?.Last24H ?? 0;
        if (glucoseTotal > 0) { typeBreakdown[nameof(SyncDataType.Glucose)] = glucoseTotal; typeBreakdown24h[nameof(SyncDataType.Glucose)] = glucose24h; }
        if (meterGlucoseTotal > 0) { typeBreakdown[nameof(SyncDataType.ManualBG)] = meterGlucoseTotal; typeBreakdown24h[nameof(SyncDataType.ManualBG)] = meterGlucose24h; }
        if (bolusesTotal > 0) { typeBreakdown[nameof(SyncDataType.Boluses)] = bolusesTotal; typeBreakdown24h[nameof(SyncDataType.Boluses)] = boluses24h; }
        if (carbIntakesTotal > 0) { typeBreakdown[nameof(SyncDataType.CarbIntake)] = carbIntakesTotal; typeBreakdown24h[nameof(SyncDataType.CarbIntake)] = carbIntakes24h; }
        if (bolusCalcsTotal > 0) { typeBreakdown[nameof(SyncDataType.BolusCalculations)] = bolusCalcsTotal; typeBreakdown24h[nameof(SyncDataType.BolusCalculations)] = bolusCalcs24h; }
        if (notesTotal > 0) { typeBreakdown[nameof(SyncDataType.Notes)] = notesTotal; typeBreakdown24h[nameof(SyncDataType.Notes)] = notes24h; }
        if (deviceEventsTotal > 0) { typeBreakdown[nameof(SyncDataType.DeviceEvents)] = deviceEventsTotal; typeBreakdown24h[nameof(SyncDataType.DeviceEvents)] = deviceEvents24h; }
        if (bgChecksTotal > 0) { typeBreakdown[nameof(SyncDataType.BGChecks)] = bgChecksTotal; typeBreakdown24h[nameof(SyncDataType.BGChecks)] = bgChecks24h; }
        if (tempBasalsTotal > 0) { typeBreakdown[nameof(SyncDataType.TempBasals)] = tempBasalsTotal; typeBreakdown24h[nameof(SyncDataType.TempBasals)] = tempBasals24h; }
        if ((stateSpanStats?.TotalStateSpans ?? 0) > 0) { typeBreakdown[nameof(SyncDataType.StateSpans)] = stateSpanStats!.TotalStateSpans; typeBreakdown24h[nameof(SyncDataType.StateSpans)] = stateSpanStats.StateSpansLast24Hours; }
        if (deviceStatusTotal > 0) { typeBreakdown[nameof(SyncDataType.DeviceStatus)] = deviceStatusTotal; typeBreakdown24h[nameof(SyncDataType.DeviceStatus)] = deviceStatus24h; }

        // Treatment totals cover exactly the types the first/last treatment timestamps span, so a
        // source with only one of them cannot report zero treatments alongside a treatment time.
        var totalTreatments = bolusesTotal + carbIntakesTotal + bolusCalcsTotal + notesTotal
            + deviceEventsTotal + bgChecksTotal + tempBasalsTotal;
        var treatments24h = boluses24h + carbIntakes24h + bolusCalcs24h + notes24h
            + deviceEvents24h + bgChecks24h + tempBasals24h;
        var lastTreatmentTime = await GetLatestTreatmentTimestampBySourceAsync(dataSource, cancellationToken);
        var firstTreatmentTime = await GetOldestTreatmentTimestampBySourceAsync(dataSource, cancellationToken);

        return new DataSourceStats(
            dataSource,
            sgStats?.Total ?? 0,
            sgStats?.Last24H ?? 0,
            sgStats?.Latest,
            sgStats?.Oldest,
            totalTreatments,
            treatments24h,
            lastTreatmentTime,
            firstTreatmentTime,
            stateSpanStats?.TotalStateSpans ?? 0,
            stateSpanStats?.StateSpansLast24Hours ?? 0,
            stateSpanStats?.LastStateSpanTime,
            stateSpanStats?.FirstStateSpanTime,
            typeBreakdown,
            typeBreakdown24h
        );
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestGlucoseTimestampBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    )
    {
        var sgTimestamp = await _sensorGlucose.GetLatestTimestampAsync(dataSource, cancellationToken);
        var mgTimestamp = await _meterGlucose.GetLatestTimestampAsync(dataSource, cancellationToken);
        var calTimestamp = await _calibrations.GetLatestTimestampAsync(dataSource, cancellationToken);

        return new[] { sgTimestamp, mgTimestamp, calTimestamp }
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .DefaultIfEmpty()
            .Max() is var max && max == default ? null : max;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetOldestGlucoseTimestampBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    )
    {
        var sgTimestamp = await _sensorGlucose.GetOldestTimestampAsync(dataSource, cancellationToken);
        var mgTimestamp = await _meterGlucose.GetOldestTimestampAsync(dataSource, cancellationToken);
        var calTimestamp = await _calibrations.GetOldestTimestampAsync(dataSource, cancellationToken);

        return new[] { sgTimestamp, mgTimestamp, calTimestamp }
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .DefaultIfEmpty()
            .Min() is var min && min == default ? null : min;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestTreatmentTimestampBySourceAsync(
        string dataSource, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var timestamps = new[]
        {
            await db.Boluses.AsNoTracking().FromSource(dataSource).OrderByDescending(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.CarbIntakes.AsNoTracking().FromSource(dataSource).OrderByDescending(c => c.Timestamp).Select(c => (DateTime?)c.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.BGChecks.AsNoTracking().FromSource(dataSource).OrderByDescending(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.Notes.AsNoTracking().FromSource(dataSource).OrderByDescending(n => n.Timestamp).Select(n => (DateTime?)n.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.DeviceEvents.AsNoTracking().FromSource(dataSource).OrderByDescending(d => d.Timestamp).Select(d => (DateTime?)d.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.TempBasals.AsNoTracking().FromSource(dataSource).OrderByDescending(t => t.Timestamp).Select(t => (DateTime?)t.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.BolusCalculations.AsNoTracking().FromSource(dataSource).OrderByDescending(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
        };
        return timestamps.Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Max() is var max && max == default ? null : max;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetOldestTreatmentTimestampBySourceAsync(
        string dataSource, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var timestamps = new[]
        {
            await db.Boluses.AsNoTracking().FromSource(dataSource).OrderBy(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.CarbIntakes.AsNoTracking().FromSource(dataSource).OrderBy(c => c.Timestamp).Select(c => (DateTime?)c.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.BGChecks.AsNoTracking().FromSource(dataSource).OrderBy(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.Notes.AsNoTracking().FromSource(dataSource).OrderBy(n => n.Timestamp).Select(n => (DateTime?)n.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.DeviceEvents.AsNoTracking().FromSource(dataSource).OrderBy(d => d.Timestamp).Select(d => (DateTime?)d.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.TempBasals.AsNoTracking().FromSource(dataSource).OrderBy(t => t.Timestamp).Select(t => (DateTime?)t.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await db.BolusCalculations.AsNoTracking().FromSource(dataSource).OrderBy(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
        };
        return timestamps.Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Min() is var min && min == default ? null : min;
    }
}
