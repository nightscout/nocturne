using Nocturne.API.Models;
using Nocturne.API.Services.Identity;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.ClientDevices;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.Services;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Lists a tenant's connections: one entry per authorized app, API token and configured connector,
/// each with the records it wrote inside the discovery window.
/// </summary>
/// <remarks>
/// A record belongs to the credential that inserted it (<c>writer_grant_id</c>), not to whatever its
/// uploader called itself, so one app is one connection however many device strings its record
/// types carry. A verified app's grants are one connection: its publisher's software id names
/// every install. Any other client is its own connection, since a client asserts its own software
/// id at registration and could name another app's.
/// </remarks>
public class ConnectionService(
    ISourceOwnedDataReader sourceData,
    IOAuthGrantRepository grants,
    IConnectorHealthService connectorHealth,
    IClientDeviceService devices,
    IDataSourceService dataSources,
    ITenantMembershipRepository members,
    ISubjectRepository subjects,
    ITenantAccessor tenant,
    IConnectorStateStore connectorState,
    TimeProvider time) : IConnectionService
{
    private const int SenderActiveMinutes = 15;
    private static readonly TimeSpan ReaderActiveWindow = TimeSpan.FromHours(24);

    /// <inheritdoc />
    public async Task<ConnectionsOverview> GetConnectionsAsync(
        Guid? viewerSubjectId, bool viewerMayKeep, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var windowStart = now.AddDays(-DataSourceService.DiscoveryWindowDays).UtcDateTime;
        var buckets = await sourceData.WriterBucketsAsync(windowStart, now.AddHours(-24).UtcDateTime, ct);
        var androidApsDevices = (await sourceData.ApsSnapshotDevicesAsync(windowStart, ct))
            .Where(d => DataSourceService.IsAndroidAps(d.AidAlgorithm))
            .Select(d => d.Device)
            .ToHashSet();

        var writerIds = buckets.Where(b => b.WriterGrantId.HasValue).Select(b => b.WriterGrantId!.Value).ToHashSet();
        var grantRecords = (await grants.ListForConnectionsAsync(viewerSubjectId, writerIds, ct))
            .Where(ConnectionKeys.NamesConnection)
            .ToList();
        var grantsById = grantRecords.ToDictionary(g => g.Id);
        var deviceCounts = await devices.GetDeviceCountsByGrantAsync([.. grantsById.Keys], ct);
        var stopping = new DeviceCredentialStops(
            await PeopleNames.OfAsync(
                members, subjects, tenant.TenantId,
                [.. grantRecords.Where(IsStoppingDeviceCredential).Select(g => g.CreatedBySubjectId!.Value).Distinct()],
                ct),
            viewerMayKeep,
            now.UtcDateTime);

        var connectorBuckets = new Dictionary<string, List<WriterBucket>>(StringComparer.OrdinalIgnoreCase);
        var otherBuckets = new List<WriterBucket>();
        var writtenBuckets = new Dictionary<string, List<WriterBucket>>();

        foreach (var bucket in buckets)
        {
            var owner = ConnectionKeys.OwnerOf(bucket.WriterGrantId, bucket.DataSource, bucket.Device, grantsById);
            if (owner.Grant is { } grant)
                Add(writtenBuckets, KeyOf(grant), bucket);
            else if (owner.Connector is { } connector)
                Add(connectorBuckets, connector.ConnectorId, bucket);
            else if (owner.Source is not null)
                otherBuckets.Add(bucket);
        }

        var folded = await FoldedGrantsAsync(grantRecords, ct);
        var connections = grantRecords
            .GroupBy(KeyOf)
            .Select(group =>
            {
                var connection = FromGrants(
                    group.Key, [.. group], writtenBuckets.GetValueOrDefault(group.Key) ?? [], deviceCounts,
                    viewerSubjectId, now, androidApsDevices, stopping);
                connection.CanDeleteRecords =
                    viewerSubjectId is { } viewer && AllOwnedBy(folded[group.Key], viewer);
                return connection;
            })
            .ToList();

        foreach (var status in await connectorHealth.GetConnectorStatusesAsync(ct))
        {
            if (ConnectorMetadataService.GetByConnectorId(status.Id)?.IsImport == true)
                continue;

            var owned = connectorBuckets.GetValueOrDefault(status.Id) ?? [];
            if (owned.Count == 0 && !status.IsEnabled && !status.HasDatabaseConfig)
                continue;

            var connection = FromConnector(status, owned, androidApsDevices);
            connection.TimeZone = await ReadTimeZoneAsync(status, ct);
            connections.Add(connection);
        }

        foreach (var import in ConnectorMetadataService.GetAll().Where(c => c.IsImport))
        {
            if (!await dataSources.ConnectorHoldsDataAsync(import.ConnectorName, ct))
                continue;

            var summary = await dataSources.GetConnectorDataSummaryAsync(import.ConnectorName, ct);
            if (summary.Total > 0)
                connections.Add(FromImport(import, summary));
        }

        return new ConnectionsOverview
        {
            Connections =
            [
                .. connections
                    .OrderBy(c => c.Status is ConnectionStatus.Quiet or ConnectionStatus.Disabled or ConnectionStatus.Revoked ? 1 : 0)
                    .ThenBy(c => c.Direction == ConnectionDirection.NoRecords ? 1 : 0)
                    .ThenByDescending(c => c.LastRecordAt ?? c.LastSyncAt ?? c.LastUsedAt)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
            ],
            OtherSources = OtherSourcesOf(otherBuckets, [.. writtenBuckets.Values.SelectMany(b => b)], androidApsDevices),
            WindowDays = DataSourceService.DiscoveryWindowDays,
            ActiveWithinMinutes = SenderActiveMinutes,
        };
    }

    /// <inheritdoc />
    public async Task<DataSourceDeleteResult> DeleteRecordsAsync(
        Guid viewerSubjectId, IReadOnlyCollection<Guid> grantIds, bool allowReimport, CancellationToken ct = default)
    {
        var named = new List<OAuthGrantRecord>();
        foreach (var grantId in grantIds.Distinct())
        {
            if (await grants.FindOfSubjectAsync(grantId, viewerSubjectId, ct) is not
                { GrantType: OAuthGrantTypes.App or OAuthGrantTypes.Direct } grant)
                return NotOwn(grantIds);
            named.Add(grant);
        }

        if (named.Select(KeyOf).Distinct().ToList() is not [var key])
            return NotOwn(grantIds);

        var connection = (await FoldedGrantsAsync(named, ct))[key];
        if (!AllOwnedBy(connection, viewerSubjectId))
            return NotOwn(grantIds);

        return await dataSources.DeleteWrittenDataAsync([.. connection.Select(g => g.Id)], allowReimport, ct);
    }

    private static DataSourceDeleteResult NotOwn(IReadOnlyCollection<Guid> grantIds) =>
        DataSourceDeleteResult.Failed(
            string.Join(',', grantIds), DataSourceDeleteError.NotFound,
            "A credential of the connection is not yours, or it is gone.");

    private static bool AllOwnedBy(IEnumerable<OAuthGrantRecord> credentials, Guid subjectId) =>
        credentials.All(g => g.SubjectId == subjectId);

    /// <summary>
    /// Every grant folded under the <see cref="KeyOf"/> key of each of <paramref name="grantsOfKeys"/>, in
    /// any state and of any subject, by key. The connections list shows only the unrevoked ones and those
    /// that wrote inside its window; a token's key holds that token alone.
    /// </summary>
    private async Task<Dictionary<string, List<OAuthGrantRecord>>> FoldedGrantsAsync(
        IReadOnlyCollection<OAuthGrantRecord> grantsOfKeys, CancellationToken ct)
    {
        var apps = grantsOfKeys.Where(g => g.GrantType == OAuthGrantTypes.App).ToList();
        IReadOnlyList<OAuthGrantRecord> clients = apps.Count == 0
            ? []
            : await grants.ListAppGrantsOfClientsAsync(
                [.. apps.Where(g => g.ClientEntityId.HasValue).Select(g => g.ClientEntityId!.Value).Distinct()],
                [.. apps.Select(g => g.Client?.SoftwareId).OfType<string>().Distinct()],
                ct);
        var keys = grantsOfKeys.Select(KeyOf).ToHashSet();
        return grantsOfKeys
            .UnionBy(clients.Where(g => keys.Contains(KeyOf(g))), g => g.Id)
            .GroupBy(KeyOf)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private static void Add(Dictionary<string, List<WriterBucket>> map, string key, WriterBucket bucket)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(bucket);
    }

    private static string KeyOf(OAuthGrantRecord grant) => ConnectionKeys.Of(grant);

    private static Connection FromGrants(
        string id,
        List<OAuthGrantRecord> credentials,
        List<WriterBucket> buckets,
        IReadOnlyDictionary<Guid, int> deviceCounts,
        Guid? viewerSubjectId,
        DateTimeOffset now,
        IReadOnlySet<string> androidApsDevices,
        DeviceCredentialStops stopping)
    {
        var newest = credentials[0];
        var isApp = newest.GrantType == OAuthGrantTypes.App;
        var lastUsed = credentials.Where(g => g.SubjectId == viewerSubjectId).Max(g => g.LastUsedAt);
        var connection = new Connection
        {
            Id = id,
            Kind = isApp ? ConnectionKind.App : ConnectionKind.Token,
            Direction = buckets.Count > 0 ? ConnectionDirection.Sends : ConnectionDirection.NoRecords,
            Name = isApp ? newest.Client?.DisplayName ?? newest.Client?.ClientId : newest.Label,
            // A self-registered client names its own logo, and loading it tells that host who opened
            // the page; only the member who authorized the app has already accepted that.
            LogoUri = newest.ClientIdentity == OAuthClientIdentity.Verified || newest.SubjectId == viewerSubjectId
                ? newest.Client?.LogoUri
                : null,
            ClientUri = newest.Client?.ClientUri,
            Identity = isApp ? newest.ClientIdentity : null,
            LastUsedAt = lastUsed is { } used ? new DateTimeOffset(used, TimeSpan.Zero) : null,
            Credentials =
            [
                .. credentials.Select(g => stopping.Describe(g, g.SubjectId == viewerSubjectId
                    ? new ConnectionCredential
                    {
                        GrantId = g.Id,
                        Label = g.Label,
                        Scopes = Scope.ForDisplay(g.Scopes, g.GrantType),
                        CreatedAt = g.CreatedAt,
                        LastUsedAt = g.LastUsedAt,
                        IsRevoked = g.IsRevoked,
                        LimitTo24Hours = g.LimitTo24Hours,
                        IsMigrated = g.IsMigrated,
                        DeviceCount = deviceCounts.GetValueOrDefault(g.Id),
                        IsOwn = true,
                    }
                    : new ConnectionCredential
                    {
                        GrantId = g.Id,
                        Label = g.Label,
                        CreatedAt = g.CreatedAt,
                        IsRevoked = g.IsRevoked,
                    })),
            ],
        };
        ApplyRecords(connection, buckets, androidApsDevices);

        connection.Status = StatusOf(connection, credentials, now);
        return connection;
    }

    /// <summary>
    /// An unrevoked device credential that a member set up and that stops when their access ends: a
    /// direct grant made for another subject, with an expiry.
    /// </summary>
    private static bool IsStoppingDeviceCredential(OAuthGrantRecord grant) =>
        grant is { GrantType: OAuthGrantTypes.Direct, RevokedAt: null, ExpiresAt: not null, CreatedBySubjectId: { } maker }
        && maker != grant.SubjectId;

    /// <summary>
    /// When each device credential of the list stops, who set it up, and whether the viewer may keep it.
    /// </summary>
    private sealed record DeviceCredentialStops(
        IReadOnlyDictionary<Guid, string?> MakerNames, bool ViewerMayKeep, DateTime Now)
    {
        public ConnectionCredential Describe(OAuthGrantRecord grant, ConnectionCredential credential)
        {
            if (!IsStoppingDeviceCredential(grant))
                return credential;

            credential.StopsAt = grant.ExpiresAt;
            credential.HasStopped = grant.ExpiresAt <= Now;
            credential.SetUpByName = MakerNames.GetValueOrDefault(grant.CreatedBySubjectId!.Value);
            credential.CanKeep = ViewerMayKeep;
            return credential;
        }
    }

    /// <summary>The data types an uploader sends continuously, so their absence means it stopped.</summary>
    private static readonly string[] ContinuousTypes = [SourceDataTypes.Glucose, SourceDataTypes.DeviceStatus];

    private static ConnectionStatus StatusOf(
        Connection connection, List<OAuthGrantRecord> credentials, DateTimeOffset now)
    {
        if (credentials.All(g => g.IsRevoked))
            return ConnectionStatus.Revoked;
        if (IsRecent(connection, now))
            return ConnectionStatus.Active;
        if (connection.Direction == ConnectionDirection.Sends)
            return ContinuousTypes.Any(connection.RecordCounts.ContainsKey)
                ? ConnectionStatus.Stopped
                : ConnectionStatus.Quiet;

        return connection.LastUsedAt is null ? ConnectionStatus.Waiting : ConnectionStatus.Quiet;
    }

    private static bool IsRecent(Connection connection, DateTimeOffset now) =>
        connection.Direction == ConnectionDirection.Sends
            ? now - connection.LastRecordAt < TimeSpan.FromMinutes(SenderActiveMinutes)
            : now - connection.LastUsedAt < ReaderActiveWindow;

    private async Task<ConnectorTimeZone?> ReadTimeZoneAsync(ConnectorStatusDto status, CancellationToken ct)
    {
        if (!status.HasDatabaseConfig)
            return null;

        if (ConnectorMetadataService.GetByConnectorId(status.Id)?.DataSourceId is not { Length: > 0 } source)
            return null;

        var stored = await connectorState.VendorAsync(source, ConnectorTimeZone.VendorStateKey, ct);
        return ConnectorTimeZone.FromStored(stored);
    }

    private static Connection FromConnector(
        ConnectorStatusDto status, List<WriterBucket> buckets, IReadOnlySet<string> androidApsDevices)
    {
        var metadata = ConnectorMetadataService.GetByConnectorId(status.Id);
        var connection = new Connection
        {
            Id = $"connector:{status.Id}",
            Kind = ConnectionKind.Connector,
            Direction = ConnectionDirection.Fetches,
            Name = metadata?.DisplayName ?? status.Name,
            Icon = metadata?.Icon,
            ConnectorId = status.Id,
            LastSyncAt = status.LastSuccessfulSync is { } sync ? new DateTimeOffset(sync, TimeSpan.Zero) : null,
            Status = status.State switch
            {
                "Error" => ConnectionStatus.Error,
                "Running" => ConnectionStatus.Active,
                "Configured" => ConnectionStatus.Quiet,
                _ => ConnectionStatus.Disabled,
            },
        };
        if (connection.Status == ConnectionStatus.Error)
            connection.ErrorMessage = status.StateMessage;

        ApplyRecords(connection, buckets, androidApsDevices);
        foreach (var stream in connection.Streams)
            stream.CanDelete = true;
        return connection;
    }

    /// <summary>
    /// An import connector is its records alone, of any age, counted as the detail dialog counts them
    /// (<see cref="IDataSourceService.GetConnectorDataSummaryAsync"/>): a configuration row or a health
    /// status is not a connection.
    /// </summary>
    private static Connection FromImport(
        ConnectorMetadataService.ConnectorDisplayInfo import, ConnectorDataSummary summary)
    {
        var id = import.ConnectorName.ToLowerInvariant();
        var connection = new Connection
        {
            Id = $"connector:{id}",
            Kind = ConnectionKind.Connector,
            Direction = ConnectionDirection.Imports,
            Status = ConnectionStatus.Imported,
            Name = import.DisplayName,
            Icon = import.Icon,
            ConnectorId = id,
            TotalRecords = summary.Total,
        };
        foreach (var (type, count) in summary.RecordCounts)
            connection.RecordCounts[type] = count;
        return connection;
    }

    /// <summary>
    /// The sources no connection wrote. One that shares an origin handle with a credential's records
    /// cannot be deleted by that handle without taking the credential's records with it.
    /// </summary>
    private static List<ConnectionStream> OtherSourcesOf(
        List<WriterBucket> unwritten, List<WriterBucket> written, IReadOnlySet<string> androidApsDevices)
    {
        var writtenHandles = written
            .SelectMany(b => new[] { b.DataSource, b.Device })
            .Where(handle => !string.IsNullOrEmpty(handle))
            .ToHashSet();

        var sources = StreamsOf(unwritten, androidApsDevices);
        foreach (var source in sources)
            source.CanDelete = !writtenHandles.Contains(source.Key);
        return sources;
    }

    private static void ApplyRecords(
        Connection connection, List<WriterBucket> buckets, IReadOnlySet<string> androidApsDevices)
    {
        connection.Streams = StreamsOf(buckets, androidApsDevices);
        connection.TotalRecords = connection.Streams.Sum(s => s.TotalRecords);
        connection.RecordsLast24Hours = connection.Streams.Sum(s => s.RecordsLast24Hours);
        connection.LastRecordAt = connection.Streams.Max(s => s.LastRecordAt);
        foreach (var (type, count) in connection.Streams.SelectMany(s => s.RecordCounts))
            connection.RecordCounts[type] = connection.RecordCounts.GetValueOrDefault(type) + count;
    }

    private static List<ConnectionStream> StreamsOf(List<WriterBucket> buckets, IReadOnlySet<string> androidApsDevices) =>
    [
        .. buckets
            .GroupBy(b => (b.DataSource, b.Device))
            .Select(group =>
            {
                var key = group.Key.DataSource ?? group.Key.Device ?? string.Empty;
                var described = DataSourceService.CreateDataSourceInfo(
                    group.Key.Device ?? key, group.Key.DataSource, SourceHandle.Unknown);
                if (group.Key.Device is { } device && androidApsDevices.Contains(device))
                    DataSourceService.DescribeAsAndroidAps(described);
                var stream = new ConnectionStream
                {
                    Key = key,
                    DataSource = group.Key.DataSource,
                    Device = group.Key.Device,
                    Name = described.Name,
                    SourceType = described.SourceType,
                    Icon = described.Icon,
                    TotalRecords = group.Sum(b => b.Count),
                    RecordsLast24Hours = group.Sum(b => b.CountLast24Hours),
                    LastRecordAt = new DateTimeOffset(group.Max(b => b.Latest), TimeSpan.Zero),
                    FirstRecordAt = new DateTimeOffset(group.Min(b => b.Oldest), TimeSpan.Zero),
                };
                foreach (var bucket in group)
                    stream.RecordCounts[bucket.DataType] = stream.RecordCounts.GetValueOrDefault(bucket.DataType) + bucket.Count;
                return stream;
            })
            .OrderByDescending(s => s.LastRecordAt),
    ];
}
