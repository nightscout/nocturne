using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Models;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Oura.Configurations;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.ClientDevices;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

[Trait("Category", "Unit")]
public class ConnectionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Viewer = Guid.NewGuid();
    private static readonly string SleepType = nameof(SyncDataType.Sleep);

    private readonly Mock<ISourceOwnedDataReader> _sourceData = new();
    private readonly Mock<IOAuthGrantRepository> _grants = new();
    private readonly Mock<IConnectorHealthService> _health = new();
    private readonly List<WriterBucket> _buckets = [];
    private readonly Dictionary<string, Dictionary<string, long>> _importCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Mock<IDataSourceService> _dataSources = new();
    private readonly List<OAuthGrantRecord> _grantRecords = [];
    private readonly List<ConnectorStatusDto> _statuses = [];
    private readonly List<ApsDeviceLastSeen> _apsDevices = [];

    private Task<ConnectionsOverview> ListAsync(IConnectorHealthService? health = null)
    {
        _sourceData
            .Setup(s => s.WriterBucketsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_buckets);
        _dataSources
            .Setup(d => d.ConnectorHoldsDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => _importCounts.ContainsKey(id));
        _dataSources
            .Setup(d => d.GetConnectorDataSummaryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new ConnectorDataSummary
            {
                ConnectorId = id,
                RecordCounts = _importCounts.GetValueOrDefault(id) ?? [],
            });
        _sourceData
            .Setup(s => s.ApsSnapshotDevicesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_apsDevices);
        _grants
            .Setup(g => g.ListForConnectionsAsync(Viewer, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_grantRecords);
        _health.Setup(h => h.GetConnectorStatusesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_statuses);

        var devices = new Mock<IClientDeviceService>();
        devices
            .Setup(d => d.GetDeviceCountsByGrantAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int>());

        return new ConnectionService(
                _sourceData.Object, _grants.Object, health ?? _health.Object, devices.Object, _dataSources.Object,
                new FakeTimeProvider(Now))
            .GetConnectionsAsync(Viewer);
    }

    private static OAuthGrantRecord Grant(
        string type,
        string? label = null,
        OAuthGrantClient? client = null,
        Guid? clientEntityId = null,
        Guid? subject = null,
        DateTime? revokedAt = null,
        DateTime? lastUsedAt = null,
        OAuthClientIdentity identity = OAuthClientIdentity.Verified) =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), clientEntityId, client, identity,
            subject ?? Viewer, null, type, ["*"], label, Now.UtcDateTime.AddDays(-10), lastUsedAt, null, null,
            revokedAt, null, null, null, null, null, false, false);

    private static WriterBucket Bucket(
        string type, Guid? writer, string? dataSource, string? device, long count, int minutesAgo = 1) =>
        new(type, writer, dataSource, device, count, (int)count,
            Now.UtcDateTime.AddMinutes(-minutesAgo), Now.UtcDateTime.AddDays(-20));

    [Fact]
    public async Task OneTokenIsOneConnectionHoweverManyDeviceStringsItsRecordsCarry()
    {
        var token = Grant(OAuthGrantTypes.Direct, "AAPS");
        _grantRecords.Add(token);
        _buckets.AddRange(
        [
            Bucket(SourceDataTypes.DeviceStatus, token.Id, null, "openaps://phone", 100),
            Bucket(SourceDataTypes.Notes, token.Id, null, "AAPS", 10),
            Bucket(SourceDataTypes.DeviceEvents, token.Id, null, "AndroidAPS", 5),
            Bucket(SourceDataTypes.Boluses, token.Id, null, null, 60),
            Bucket(SourceDataTypes.Glucose, token.Id, null, "Unknown", 500),
        ]);

        var overview = await ListAsync();

        var connection = overview.Connections.Should().ContainSingle().Subject;
        connection.Kind.Should().Be(ConnectionKind.Token);
        connection.Name.Should().Be("AAPS");
        connection.Direction.Should().Be(ConnectionDirection.Sends);
        connection.Status.Should().Be(ConnectionStatus.Active);
        connection.TotalRecords.Should().Be(675);
        connection.Streams.Should().HaveCount(5);
        connection.RecordCounts[SourceDataTypes.Boluses].Should().Be(60);
        overview.OtherSources.Should().BeEmpty();
    }

    [Theory]
    [InlineData("AndroidAps", "AndroidAPS")]
    [InlineData("OpenAps", "OpenAPS")]
    public async Task ALoopStatusStreamIsNamedByItsNewestSnapshotsAlgorithm(string aidAlgorithm, string name)
    {
        var token = Grant(OAuthGrantTypes.Direct, "AAPS");
        _grantRecords.Add(token);
        _buckets.Add(Bucket(SourceDataTypes.DeviceStatus, token.Id, null, "openaps://phone", 100));
        _apsDevices.Add(new ApsDeviceLastSeen("openaps://phone", null, Now.UtcDateTime, aidAlgorithm));

        var overview = await ListAsync();

        var stream = overview.Connections.Should().ContainSingle().Subject.Streams.Should().ContainSingle().Subject;
        stream.Name.Should().Be(name);
    }

    [Fact]
    public async Task TwoWritersOfTheSameDeviceStringStayApart()
    {
        var token = Grant(OAuthGrantTypes.Direct, "AAPS");
        var app = Grant(OAuthGrantTypes.App, client: new OAuthGrantClient("c1", "Prelude", null, null, "dev.prelude"));
        _grantRecords.AddRange([token, app]);
        _buckets.AddRange(
        [
            Bucket(SourceDataTypes.Glucose, token.Id, null, "Unknown", 300),
            Bucket(SourceDataTypes.Glucose, app.Id, "prelude:companion", "Unknown", 200),
        ]);

        var overview = await ListAsync();

        overview.Connections.Should().HaveCount(2);
        overview.Connections.Single(c => c.Kind == ConnectionKind.Token).TotalRecords.Should().Be(300);
        overview.Connections.Single(c => c.Kind == ConnectionKind.App).TotalRecords.Should().Be(200);
    }

    [Fact]
    public async Task GrantsOfOneSoftwareIdAreOneConnectionAcrossClientRegistrations()
    {
        var revoked = Grant(
            OAuthGrantTypes.App, client: new OAuthGrantClient("c1", "Prelude", null, null, "dev.prelude"),
            clientEntityId: Guid.NewGuid(), revokedAt: Now.UtcDateTime.AddDays(-5));
        var live = Grant(
            OAuthGrantTypes.App, client: new OAuthGrantClient("c2", "Prelude", null, null, "dev.prelude"),
            clientEntityId: Guid.NewGuid());
        _grantRecords.AddRange([live, revoked]);
        _buckets.AddRange(
        [
            Bucket(SourceDataTypes.Glucose, revoked.Id, "prelude", "companion", 40, minutesAgo: 60 * 24 * 6),
            Bucket(SourceDataTypes.Glucose, live.Id, "prelude:libre3:X", "libre3 (X)", 90),
        ]);

        var overview = await ListAsync();

        var connection = overview.Connections.Should().ContainSingle().Subject;
        connection.Name.Should().Be("Prelude");
        connection.Credentials.Should().HaveCount(2);
        connection.Status.Should().Be(ConnectionStatus.Active);
        connection.Streams.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnUnverifiedClientIsNotFoldedIntoTheAppWhoseSoftwareIdItClaims()
    {
        _grantRecords.AddRange(
        [
            Grant(OAuthGrantTypes.App, client: new OAuthGrantClient("real", "AndroidAPS", null, null, "info.aaps"),
                clientEntityId: Guid.NewGuid()),
            Grant(OAuthGrantTypes.App, client: new OAuthGrantClient("fake", "AndroidAPS", null, null, "info.aaps"),
                clientEntityId: Guid.NewGuid(), identity: OAuthClientIdentity.SelfRegistered),
        ]);

        var overview = await ListAsync();

        overview.Connections.Should().HaveCount(2);
        overview.Connections.Should().OnlyContain(c => c.Credentials.Count == 1);
    }

    [Fact]
    public async Task ASourceThatSharesAHandleWithACredentialCannotBeDeletedByIt()
    {
        var token = Grant(OAuthGrantTypes.Direct, "xDrip");
        _grantRecords.Add(token);
        _buckets.AddRange(
        [
            Bucket(SourceDataTypes.Glucose, token.Id, null, "xDrip-DexcomG6", 30),
            Bucket(SourceDataTypes.Glucose, null, null, "xDrip-DexcomG6", 400),
            Bucket(SourceDataTypes.Glucose, null, null, "Spike", 20),
        ]);

        var overview = await ListAsync();

        overview.OtherSources.Single(s => s.Key == "xDrip-DexcomG6").CanDelete.Should().BeFalse();
        overview.OtherSources.Single(s => s.Key == "Spike").CanDelete.Should().BeTrue();
        overview.Connections.Single().Streams.Should().OnlyContain(s => !s.CanDelete);
    }

    [Fact]
    public async Task AConnectorThatIsNoLongerConfiguredIsListedWhileItsRecordsRemain()
    {
        _statuses.Add(new ConnectorStatusDto { Id = "glooko", Name = "glooko", Status = "Not Configured", State = "Not Configured" });
        _buckets.Add(Bucket(SourceDataTypes.Glucose, null, "glooko-connector", "glooko-connector", 70, minutesAgo: 600));

        var overview = await ListAsync();

        var connector = overview.Connections.Should().ContainSingle().Subject;
        connector.Status.Should().Be(ConnectionStatus.Disabled);
        connector.Streams.Should().ContainSingle().Which.CanDelete.Should().BeTrue();
        overview.OtherSources.Should().BeEmpty();
    }

    [Fact]
    public async Task AnImportIsListedWithTheCountsTheDetailDialogShows_WhateverTheAgeOfItsRecords()
    {
        _ = typeof(OuraConnectorConfiguration);
        _importCounts["oura"] = new()
        {
            [SourceDataTypes.HeartRate] = 500, [SourceDataTypes.Steps] = 40, [SleepType] = 12,
        };

        var overview = await ListAsync();

        var connector = overview.Connections.Should().ContainSingle().Subject;
        connector.Id.Should().Be("connector:oura");
        connector.Status.Should().Be(ConnectionStatus.Imported);
        connector.Direction.Should().Be(ConnectionDirection.Imports);
        connector.LastSyncAt.Should().BeNull();
        connector.TotalRecords.Should().Be(552);
        connector.RecordCounts.Should().BeEquivalentTo(_importCounts["oura"]);
    }

    [Fact]
    public async Task AnImportWithOnlySleepSessionsIsListed()
    {
        _ = typeof(OuraConnectorConfiguration);
        _importCounts["oura"] = new() { [SleepType] = 3 };

        var overview = await ListAsync();

        overview.Connections.Should().ContainSingle().Which.TotalRecords.Should().Be(3);
    }

    [Fact]
    public async Task AnImportIsListedOnceWhateverTheHealthServiceReportsOfIt()
    {
        _ = typeof(OuraConnectorConfiguration);
        _statuses.Add(new ConnectorStatusDto
        {
            Id = "oura", Name = "oura", Status = "Running", State = "Running", IsEnabled = true,
            LastSuccessfulSync = Now.UtcDateTime.AddDays(-3),
        });
        _importCounts["oura"] = new() { [SourceDataTypes.HeartRate] = 500 };

        var overview = await ListAsync();

        overview.Connections.Should().ContainSingle().Which.Direction.Should().Be(ConnectionDirection.Imports);
    }

    [Fact]
    public async Task AnImportTheRealHealthServiceDropsIsStillListed()
    {
        _ = typeof(OuraConnectorConfiguration);
        _importCounts["oura"] = new() { [SourceDataTypes.HeartRate] = 500, [SleepType] = 12 };
        var configurations = new Mock<IConnectorConfigurationService>();
        configurations
            .Setup(c => c.GetAllConnectorStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        configurations
            .Setup(c => c.GetConfigurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectorConfigurationResponse?)null);
        _dataSources
            .Setup(d => d.GetDataSourceStatsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string dataSource, CancellationToken _) => new DataSourceStats(
                dataSource, 0, 0, null, null, 0, 0, null, null, 0, 0, null, null, [], []));
        var health = new ConnectorHealthService(
            new ConfigurationBuilder().Build(), _dataSources.Object, configurations.Object,
            NullLogger<ConnectorHealthService>.Instance);

        (await health.GetConnectorStatusesAsync()).Should().NotContain(s => s.Id == "oura");

        var overview = await ListAsync(health);

        overview.Connections.Should().ContainSingle().Which.TotalRecords.Should().Be(512);
    }

    [Fact]
    public async Task ATenantThatNeverImportedRunsNoSummary()
    {
        _ = typeof(OuraConnectorConfiguration);

        var overview = await ListAsync();

        overview.Connections.Should().BeEmpty();
        _dataSources.Verify(
            d => d.ConnectorHoldsDataAsync("Oura", It.IsAny<CancellationToken>()), Times.Once);
        _dataSources.Verify(
            d => d.GetConnectorDataSummaryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AFetchingConnectorOlderThanTheWindowIsStillNotListed_AndIsNotReadForItsWholeHistory()
    {
        _statuses.Add(new ConnectorStatusDto { Id = "dexcom", Name = "dexcom", Status = "Not Configured", State = "Not Configured" });

        var overview = await ListAsync();

        overview.Connections.Should().BeEmpty();
        _dataSources.Verify(
            d => d.GetConnectorDataSummaryAsync("dexcom", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AConnectorWithNoSyncAndNoRecordsIsNotListed_EvenWithAConfigurationRow()
    {
        _ = typeof(OuraConnectorConfiguration);
        _statuses.Add(new ConnectorStatusDto
        {
            Id = "oura", Name = "oura", Status = "Disabled", State = "Disabled", HasDatabaseConfig = true,
        });

        var overview = await ListAsync();

        overview.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task AppsWithoutASoftwareIdAreSeparateConnectionsPerClient()
    {
        _grantRecords.AddRange(
        [
            Grant(OAuthGrantTypes.App, client: new OAuthGrantClient("a", "Home Assistant", null, null), clientEntityId: Guid.NewGuid()),
            Grant(OAuthGrantTypes.App, client: new OAuthGrantClient("b", "Home Assistant", null, null), clientEntityId: Guid.NewGuid()),
        ]);

        var overview = await ListAsync();

        overview.Connections.Should().HaveCount(2);
        overview.Connections.Should().OnlyContain(c => c.Direction == ConnectionDirection.NoRecords);
    }

    [Fact]
    public async Task ACredentialThatOnlyReadsIsActiveWhileItIsInUse()
    {
        _grantRecords.Add(Grant(OAuthGrantTypes.Direct, "Reader", lastUsedAt: Now.UtcDateTime.AddHours(-2)));
        _grantRecords.Add(Grant(OAuthGrantTypes.Direct, "Idle", lastUsedAt: Now.UtcDateTime.AddDays(-3)));

        var overview = await ListAsync();

        overview.Connections.Single(c => c.Name == "Reader").Status.Should().Be(ConnectionStatus.Active);
        overview.Connections.Single(c => c.Name == "Idle").Status.Should().Be(ConnectionStatus.Quiet);
    }

    [Fact]
    public async Task AnActiveSenderIsListedAheadOfAnActiveReader()
    {
        var sender = Grant(OAuthGrantTypes.Direct, "Sender");
        _grantRecords.Add(Grant(OAuthGrantTypes.Direct, "Reader", lastUsedAt: Now.UtcDateTime));
        _grantRecords.Add(sender);
        _buckets.Add(Bucket(SourceDataTypes.Glucose, sender.Id, null, "xDrip", 12, minutesAgo: 5));

        var overview = await ListAsync();

        overview.Connections.Select(c => c.Name).Should().Equal("Sender", "Reader");
    }

    [Fact]
    public async Task AnUploaderOfContinuousDataThatWentSilentIsStopped()
    {
        var uploader = Grant(OAuthGrantTypes.Direct, "Phone");
        _grantRecords.Add(uploader);
        _buckets.Add(Bucket(SourceDataTypes.Glucose, uploader.Id, null, "xDrip", 12, minutesAgo: 20));

        var overview = await ListAsync();

        overview.Connections.Single().Status.Should().Be(ConnectionStatus.Stopped);
    }

    [Fact]
    public async Task TheActiveThresholdTheOverviewStatesIsTheOneStatusUses()
    {
        var uploader = Grant(OAuthGrantTypes.Direct, "Phone");
        _grantRecords.Add(uploader);
        var minutes = (await ListAsync()).ActiveWithinMinutes;
        _buckets.Add(Bucket(SourceDataTypes.Glucose, uploader.Id, null, "xDrip", 12, minutesAgo: minutes - 1));
        (await ListAsync()).Connections.Single().Status.Should().Be(ConnectionStatus.Active);

        _buckets.Clear();
        _buckets.Add(Bucket(SourceDataTypes.Glucose, uploader.Id, null, "xDrip", 12, minutesAgo: minutes + 1));
        (await ListAsync()).Connections.Single().Status.Should().Be(ConnectionStatus.Stopped);
    }

    [Fact]
    public async Task ASenderOfOccasionalRecordsIsQuietBetweenThem()
    {
        var logger = Grant(OAuthGrantTypes.Direct, "Meal logger");
        _grantRecords.Add(logger);
        _buckets.Add(Bucket(SourceDataTypes.CarbIntake, logger.Id, null, "Meals", 3, minutesAgo: 600));

        var overview = await ListAsync();

        overview.Connections.Single().Status.Should().Be(ConnectionStatus.Quiet);
    }

    [Fact]
    public async Task ACredentialNotUsedYetIsWaiting()
    {
        _grantRecords.Add(Grant(OAuthGrantTypes.Direct, "New"));

        var overview = await ListAsync();

        overview.Connections.Single().Status.Should().Be(ConnectionStatus.Waiting);
    }

    [Fact]
    public async Task ARevokedCredentialWithRecordsIsListedAsRevoked()
    {
        var revoked = Grant(OAuthGrantTypes.Direct, "Old", revokedAt: Now.UtcDateTime.AddDays(-1));
        _grantRecords.Add(revoked);
        _buckets.Add(Bucket(SourceDataTypes.Glucose, revoked.Id, null, "xDrip", 12, minutesAgo: 60 * 30));

        var overview = await ListAsync();

        overview.Connections.Should().ContainSingle().Which.Status.Should().Be(ConnectionStatus.Revoked);
    }

    [Fact]
    public async Task AnotherMembersCredentialIsNotTheViewersToRevoke()
    {
        var theirs = Grant(
            OAuthGrantTypes.Direct, "Theirs", subject: Guid.NewGuid(), lastUsedAt: Now.UtcDateTime.AddMinutes(-3));
        _grantRecords.Add(theirs);
        _buckets.Add(Bucket(SourceDataTypes.Glucose, theirs.Id, null, "xDrip", 12));

        var overview = await ListAsync();

        var credential = overview.Connections.Single().Credentials.Single();
        credential.IsOwn.Should().BeFalse();
        credential.Scopes.Should().BeEmpty();
        credential.LastUsedAt.Should().BeNull();
        overview.Connections.Single().LastUsedAt.Should().BeNull();
    }

    [Fact]
    public async Task RecordsWithNoWriterGoToTheirConnectorOrToOtherSources()
    {
        _statuses.Add(new ConnectorStatusDto
        {
            Id = "glooko", Name = "glooko", Status = "Running", State = "Running", IsEnabled = true,
            HasDatabaseConfig = true, LastSuccessfulSync = Now.UtcDateTime.AddMinutes(-1),
        });
        _statuses.Add(new ConnectorStatusDto { Id = "dexcom", Name = "dexcom", Status = "Not Configured", State = "Not Configured" });
        _buckets.AddRange(
        [
            Bucket(SourceDataTypes.Glucose, null, "glooko-connector", "glooko-connector", 70, minutesAgo: 60 * 24 * 3),
            Bucket(SourceDataTypes.StateSpans, null, null, "glooko-connector", 4, minutesAgo: 60 * 24 * 3),
            Bucket(SourceDataTypes.Glucose, null, null, "xDrip-DexcomG6", 30),
            Bucket(SourceDataTypes.Boluses, null, null, null, 9),
        ]);

        var overview = await ListAsync();

        var connector = overview.Connections.Should().ContainSingle().Subject;
        connector.Kind.Should().Be(ConnectionKind.Connector);
        connector.ConnectorId.Should().Be("glooko");
        connector.Direction.Should().Be(ConnectionDirection.Fetches);
        connector.Status.Should().Be(ConnectionStatus.Active);
        connector.TotalRecords.Should().Be(74);
        connector.LastSyncAt.Should().Be(Now.AddMinutes(-1));

        var other = overview.OtherSources.Should().ContainSingle().Subject;
        other.Key.Should().Be("xDrip-DexcomG6");
        other.TotalRecords.Should().Be(30);
    }

    [Fact]
    public async Task AConnectorErrorCarriesItsReason()
    {
        _statuses.Add(new ConnectorStatusDto
        {
            Id = "myfitnesspal", Name = "myfitnesspal", Status = "Error", State = "Error", IsEnabled = true,
            HasDatabaseConfig = true, StateMessage = "Sign-in was rejected",
        });

        var overview = await ListAsync();

        var connector = overview.Connections.Single();
        connector.Status.Should().Be(ConnectionStatus.Error);
        connector.ErrorMessage.Should().Be("Sign-in was rejected");
    }
}
