using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Notifications;
using Nocturne.API.Services.Platform.InstanceKey;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Shared.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform.InstanceKey;

public sealed class InstanceKeyChangeServiceTests : IDisposable
{
    private static readonly Guid Affected = Guid.NewGuid();
    private static readonly Guid Unaffected = Guid.NewGuid();

    private readonly ISecretEncryptionService _oldKey = Encryption("the-old-instance-key");
    private readonly ISecretEncryptionService _newKey = Encryption("the-new-instance-key");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDatabase _database = TestDbContextFactory.CreateSqlite();
    private readonly List<Sent> _sent = [];
    private readonly List<InAppNotificationEntity> _active = [];
    private readonly List<(string UserId, string Type, string SourceId, NotificationArchiveReason Reason)> _archived = [];
    private readonly Dictionary<Guid, string[]> _owners = [];
    private readonly ListLogger _log = new();
    private ISecretEncryptionService _key;
    private string? _failFor;
    private readonly ServiceProvider _provider;

    public InstanceKeyChangeServiceTests()
    {
        _key = _newKey;
        _database.SeedTenant(Affected, "affected").SeedTenant(Unaffected, "unaffected");

        var owners = new Mock<ITenantOwnerResolver>();
        owners.Setup(o => o.GetOwnerSubjectIdsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid tenantId, CancellationToken _) => OwnersOf(tenantId));

        var repository = new Mock<IInAppNotificationRepository>();
        repository
            .Setup(r => r.GetActiveBySourceAsync(It.IsAny<string>(), InstanceKeyChangeService.Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string userId, string _, CancellationToken _) => _active.Where(n => n.UserId == userId).ToList());

        var services = new ServiceCollection();
        services.AddSingleton(_database.ContextFactory);
        services.AddScoped<ITenantAccessor, StubTenantAccessor>();
        services.AddScoped(sp => _database.ContextFactory.AsNocturneFactory(sp.GetRequiredService<ITenantAccessor>().TenantId));
        services.AddScoped(_ => owners.Object);
        services.AddScoped(_ => repository.Object);
        services.AddScoped<IInAppNotificationService>(sp =>
            new RecordingNotifications(sp.GetRequiredService<ITenantAccessor>(), this));
        services.AddTransient(_ => _key);
        services.AddTransient<UndecryptableSecretScanner>();
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task TheFirstStartWritesTheCheckAndARestartWithTheSameKeySendsNothing()
    {
        await StoreConnectorAsync(Affected, "dexcom", _newKey);

        await RunAsync(CreateService());
        var written = await CheckAsync();
        await RunAsync(CreateService());

        written.Should().NotBeNull();
        written!.Outstanding.Should().BeFalse();
        (await CheckAsync())!.Generation.Should().Be(written.Generation);
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task EachOwnerOfAnAffectedTenantHearsOnceAboutEachItemToReEnter()
    {
        await StoreConnectorAsync(Affected, "dexcom", _oldKey);
        var ruleId = await StoreWebhookChannelAsync(Affected, "High glucose", "https://hooks.example.com/a?token=x", _oldKey);
        await StoreConnectorAsync(Unaffected, "glooko", _newKey);
        var generation = await DetectAsync();

        var service = CreateService();
        (await service.ReconcileAsync(generation, CancellationToken.None)).Should().BeFalse();
        await service.ReconcileAsync(generation, CancellationToken.None);
        await CreateService().ReconcileAsync(generation, CancellationToken.None);

        _sent.Should().HaveCount(2).And.OnlyContain(s =>
            s.TenantId == Affected
            && s.Type == InstanceKeyChangeService.TenantNoticeType
            && s.Urgency == NotificationUrgency.Urgent
            && s.SourceId == generation.ToString());
        _sent.Select(s => s.UserId).Should().BeEquivalentTo(OwnersOf(Affected));

        var notice = _sent[0];
        notice.Actions.Select(a => a.ActionId).Should().Equal(
            InstanceKeyChangeService.OpenConnectorsActionId, InstanceKeyChangeService.OpenAlertsActionId);
        ((IEnumerable<string>)notice.Metadata["Connectors"]).Should().ContainSingle()
            .Which.Should().Be(DisplayName("dexcom"));
        ((IEnumerable<UndecryptableAlertChannel>)notice.Metadata["AlertChannels"]).Should().Equal(
            new UndecryptableAlertChannel(ruleId, "High glucose", "hooks.example.com"));
    }

    [Fact]
    public async Task TheAlertChannelsAreStoredInTheShapeTheGeneratedClientReads()
    {
        await StoreWebhookChannelAsync(Affected, "High glucose", "https://hooks.example.com/a", _oldKey);
        var generation = await DetectAsync();

        await CreateService().ReconcileAsync(generation, CancellationToken.None);

        var json = InAppNotificationRepository.SerializeMetadata(_sent[0].Metadata)!;
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("AlertChannels")[0].EnumerateObject().Select(p => p.Name)
            .Should().Equal("ruleId", "ruleName", "destination");
    }

    [Fact]
    public async Task ADestinationThatIsNotAUrlIsLeftOutOfTheNotice()
    {
        await StoreWebhookChannelAsync(Affected, "Bare", "hooks/token-in-the-path", _oldKey);
        await StoreWebhookChannelAsync(Affected, "Mixed", "https://a.example.com/x, not a url", _oldKey);

        var found = await new UndecryptableSecretScanner(
            _database.ContextFactory.AsNocturneFactory(Affected), _newKey).ScanTenantAsync(CancellationToken.None);

        found.AlertChannels.Select(c => (c.RuleName, c.Destination)).Should().Equal(
            ("Bare", ""), ("Mixed", "a.example.com"));
    }

    [Fact]
    public async Task AConnectorAloneIsAWarningThatOpensTheConnectors()
    {
        await StoreConnectorAsync(Affected, "dexcom", _oldKey);
        var generation = await DetectAsync();

        await CreateService().ReconcileAsync(generation, CancellationToken.None);

        _sent.Should().OnlyContain(s => s.Urgency == NotificationUrgency.Warn
            && s.Actions.Single().ActionId == InstanceKeyChangeService.OpenConnectorsActionId);
    }

    [Fact]
    public async Task EachPlatformAdminHearsOnceInTheTenantTheyJoinedFirst()
    {
        await StorePlatformSettingAsync("discord", enabled: true, _oldKey);
        await StorePlatformSettingAsync("slack", enabled: false, _newKey);
        var admin = await SeedPlatformAdminAsync(Unaffected, Affected);
        var generation = await DetectAsync();

        await CreateService().ReconcileAsync(generation, CancellationToken.None);
        await CreateService().ReconcileAsync(generation, CancellationToken.None);

        var notice = _sent.Should().ContainSingle().Subject;
        notice.Should().Match<Sent>(s => s.UserId == admin.ToString()
            && s.TenantId == Unaffected
            && s.Type == InstanceKeyChangeService.PlatformNoticeType
            && s.Urgency == NotificationUrgency.Urgent);
        ((IEnumerable<string>)notice.Metadata["PlatformSettings"]).Should().Equal("discord");
    }

    [Fact]
    public async Task APlatformAdminInNoActiveTenantIsLoggedRatherThanTold()
    {
        var inactive = Guid.NewGuid();
        _database.SeedTenant(inactive, "inactive", isActive: false);
        var reachable = await SeedPlatformAdminAsync(Affected);
        await SeedPlatformAdminAsync();
        await SeedPlatformAdminAsync(inactive);

        var admins = await CreateService().PlatformAdminsAsync(CancellationToken.None);

        admins.Should().ContainSingle().Which.Should().Match<InstanceKeyChangeService.PlatformAdmin>(a =>
            a.SubjectId == reachable && a.TenantId == Affected && a.Slug == "affected");
        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().StartWith("2 platform admins");
    }

    [Fact]
    public async Task ReEnteringEverySecretArchivesTheNoticesAndQuietsTheNextStart()
    {
        await WriteCheckAsync(_oldKey);
        await StoreConnectorAsync(Affected, "dexcom", _oldKey);
        await StoreWebhookChannelAsync(Affected, "High glucose", "https://hooks.example.com/a", _oldKey);
        await StorePlatformSettingAsync("discord", enabled: true, _oldKey);
        var admin = await SeedPlatformAdminAsync(Affected);

        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _sent.Count == OwnersOf(Affected).Length + 1);
        var generation = (await CheckAsync())!.Generation.ToString();

        await StoreConnectorAsync(Affected, "dexcom", _newKey);
        await using (var db = _database.CreateContext(Affected))
            await db.AlertRuleChannels.ExecuteDeleteAsync();
        await StorePlatformSettingAsync("discord", enabled: true, _newKey);
        await AdvanceUntilAsync(() => service.ExecuteTask!.IsCompleted);
        await service.ExecuteTask!;

        _archived.Should().BeEquivalentTo(
            OwnersOf(Affected).Select(o => (o, InstanceKeyChangeService.TenantNoticeType, generation, NotificationArchiveReason.Completed))
                .Append((admin.ToString(), InstanceKeyChangeService.PlatformNoticeType, generation, NotificationArchiveReason.Completed)));
        (await CheckAsync())!.Outstanding.Should().BeFalse();

        _sent.Clear();
        await RunAsync(CreateService());
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ATenantStillAffectedKeepsItsNoticeAndTheChange()
    {
        await StoreConnectorAsync(Affected, "dexcom", _oldKey);
        await StoreConnectorAsync(Affected, "glooko", _oldKey);
        var generation = await DetectAsync();
        var service = CreateService();
        await service.ReconcileAsync(generation, CancellationToken.None);

        await StoreConnectorAsync(Affected, "dexcom", _newKey);

        (await service.ReconcileAsync(generation, CancellationToken.None)).Should().BeFalse();
        _archived.Should().BeEmpty();
        (await CheckAsync())!.Should().Match<InstanceKeyCheckEntity>(c => c.Generation == generation && c.Outstanding);
    }

    [Fact]
    public async Task AKeyChangedAgainBeforeTheFirstIsClearedTellsTheOwnersAgain()
    {
        var third = Encryption("a-third-instance-key");
        await WriteCheckAsync(_oldKey);
        await StoreConnectorAsync(Affected, "dexcom", _oldKey);
        await StoreConnectorAsync(Unaffected, "dexcom", _oldKey);

        var first = CreateService();
        await first.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _sent.Count == 4);
        var firstChange = (await CheckAsync())!.Generation.ToString();
        await StoreConnectorAsync(Affected, "dexcom", _newKey);
        await AdvanceUntilAsync(() => _archived.Count == 2);
        await first.StopAsync(CancellationToken.None);
        _sent.Clear();
        _archived.Clear();

        _key = third;
        var second = CreateService();
        await second.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _sent.Count == 4);
        await second.StopAsync(CancellationToken.None);

        var secondChange = (await CheckAsync())!.Generation.ToString();
        secondChange.Should().NotBe(firstChange);
        _sent.Where(s => s.TenantId == Affected).Select(s => s.UserId).Should().BeEquivalentTo(OwnersOf(Affected));
        _sent.Should().OnlyContain(s => s.SourceId == secondChange);
        _archived.Should().BeEquivalentTo(OwnersOf(Unaffected)
            .Select(o => (o, InstanceKeyChangeService.TenantNoticeType, firstChange, NotificationArchiveReason.Superseded)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedPassIsTriedAgainOnTheNextScan(bool cancelledElsewhere)
    {
        await WriteCheckAsync(_oldKey);
        await StoreConnectorAsync(Affected, "dexcom", _oldKey);
        var flaky = new FailingFirstFactory(
            _database.ContextFactory,
            cancelledElsewhere ? new OperationCanceledException("a timeout") : new InvalidOperationException("database not up"));

        var service = CreateService(snapshotContexts: flaky.AsNocturneFactory());
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => flaky.Failed);
        service.ExecuteTask!.IsCompleted.Should().BeFalse();

        await AdvanceUntilAsync(() => _sent.Count == OwnersOf(Affected).Length);
        await service.StopAsync(CancellationToken.None);

        _log.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task AReplicaOnTheReplacedKeyNeitherTakesTheCheckNorNotifiesAgain()
    {
        await WriteCheckAsync(_oldKey);
        await StoreConnectorAsync(Affected, "dexcom", _oldKey);

        for (var pass = 0; pass < 3; pass++)
        {
            _key = _newKey;
            (await CreateService().PassAsync(CancellationToken.None)).Should().BeFalse();
            _key = _oldKey;
            (await CreateService().PassAsync(CancellationToken.None)).Should().BeFalse();
        }

        _log.Entries.Count(e => e.Level == LogLevel.Error && e.Message.Contains("two instance keys")).Should().Be(3);
        _sent.Select(s => s.UserId).Should().BeEquivalentTo(OwnersOf(Affected), "one key change is one notice per owner");
        _archived.Should().BeEmpty();
        (await CheckAsync())!.Outstanding.Should().BeTrue();
    }

    [Fact]
    public async Task ThePreviousKeyComingBackAfterTheNewerOneStoppedIsOneMoreChange()
    {
        await WriteCheckAsync(_oldKey);
        await StoreConnectorAsync(Affected, "dexcom", _oldKey);
        await StoreConnectorAsync(Unaffected, "dexcom", _oldKey);
        _key = _newKey;
        await CreateService().PassAsync(CancellationToken.None);
        var newerChange = (await CheckAsync())!.Generation.ToString();
        await StoreConnectorAsync(Affected, "dexcom", _newKey);
        await using (var db = _database.CreateContext())
            await db.InstanceKeyChecks.ExecuteUpdateAsync(u => u.SetProperty(
                c => c.CheckedAt, DateTime.UtcNow - InstanceKeyCheck.LivenessWindow - TimeSpan.FromMinutes(1)));
        _sent.Clear();

        _key = _oldKey;
        var service = CreateService();
        (await service.PassAsync(CancellationToken.None)).Should().BeFalse();
        await service.PassAsync(CancellationToken.None);

        var rollback = (await CheckAsync())!.Generation.ToString();
        rollback.Should().NotBe(newerChange);
        _sent.Should().OnlyContain(s => s.TenantId == Affected && s.SourceId == rollback)
            .And.Subject.Select(s => s.UserId).Should().BeEquivalentTo(OwnersOf(Affected), "once per owner");
        _archived.Should().BeEquivalentTo(
            OwnersOf(Affected).Select(o => (o, InstanceKeyChangeService.TenantNoticeType, newerChange, NotificationArchiveReason.Superseded))
                .Concat(OwnersOf(Unaffected).Select(o => (o, InstanceKeyChangeService.TenantNoticeType, newerChange, NotificationArchiveReason.Completed))));
        _log.Entries.Should().NotContain(e => e.Message.Contains("two instance keys"));
    }

    [Fact]
    public async Task AFailedSendIsSentOnTheNextScan()
    {
        await StoreConnectorAsync(Affected, "dexcom", _oldKey);
        var generation = await DetectAsync();
        _failFor = OwnersOf(Affected)[1];
        var service = CreateService();

        await service.ReconcileAsync(generation, CancellationToken.None);
        _failFor = null;
        _sent.Select(s => s.UserId).Should().Equal(OwnersOf(Affected)[0]);

        await service.ReconcileAsync(generation, CancellationToken.None);
        _sent.Select(s => s.UserId).Should().Equal(OwnersOf(Affected));
    }

    private InstanceKeyChangeService CreateService(INocturneDbContextFactory? snapshotContexts = null)
    {
        var root = _database.ContextFactory.AsNocturneFactory();
        return new InstanceKeyChangeService(
            _provider,
            new InstanceKeyCheck(root, _key),
            new UndecryptableSecretScanner(root, _key),
            new OwnerNoticeLedger(root),
            new ActiveTenantSnapshot(snapshotContexts ?? root, _clock, NullLogger<ActiveTenantSnapshot>.Instance),
            root,
            _clock,
            _log);
    }

    private static async Task RunAsync(InstanceKeyChangeService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline);
            await Task.Delay(10);
        }
    }

    /// <summary>Moves the clock a scan at a time, since a scan may not yet be waiting when it moves.</summary>
    private Task AdvanceUntilAsync(Func<bool> condition) => WaitUntilAsync(() =>
    {
        if (condition())
            return true;
        _clock.Advance(InstanceKeyChangeService.RescanInterval);
        return false;
    });

    private async Task WriteCheckAsync(ISecretEncryptionService key) =>
        (await new InstanceKeyCheck(_database.ContextFactory.AsNocturneFactory(), key)
            .ChangeAsync()).Should().BeNull();

    /// <summary>A check written under the old key, found by the current one.</summary>
    private async Task<Guid> DetectAsync()
    {
        await WriteCheckAsync(_oldKey);
        return (await new InstanceKeyCheck(_database.ContextFactory.AsNocturneFactory(), _key)
            .ChangeAsync())!.Value;
    }

    private async Task<InstanceKeyCheckEntity?> CheckAsync()
    {
        await using var db = _database.CreateContext();
        return await db.InstanceKeyChecks.AsNoTracking().SingleOrDefaultAsync();
    }

    private async Task StoreConnectorAsync(Guid tenantId, string name, ISecretEncryptionService key)
    {
        await using var db = _database.CreateContext(tenantId);
        var secrets = JsonSerializer.Serialize(key.EncryptSecrets(new() { ["password"] = "hunter2" }));
        var existing = await db.ConnectorConfigurations.SingleOrDefaultAsync(c => c.ConnectorName == name);
        if (existing is null)
            db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
            {
                Id = Guid.NewGuid(), TenantId = tenantId, ConnectorName = name, SecretsJson = secrets,
            });
        else
            existing.SecretsJson = secrets;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> StoreWebhookChannelAsync(Guid tenantId, string ruleName, string url, ISecretEncryptionService key)
    {
        await using var db = _database.CreateContext(tenantId);
        var rule = new AlertRuleEntity { Id = Guid.NewGuid(), TenantId = tenantId, Name = ruleName };
        rule.Channels.Add(new AlertRuleChannelEntity
        {
            Id = Guid.NewGuid(), TenantId = tenantId, AlertRuleId = rule.Id, ChannelType = ChannelType.Webhook,
            Destination = url, Secret = key.Encrypt("signing-secret"),
        });
        rule.Channels.Add(new AlertRuleChannelEntity
        {
            Id = Guid.NewGuid(), TenantId = tenantId, AlertRuleId = rule.Id, ChannelType = ChannelType.Webhook,
            Destination = "https://unsigned.example.com", SortOrder = 1,
        });
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    private async Task StorePlatformSettingAsync(string category, bool enabled, ISecretEncryptionService key)
    {
        await using var db = _database.CreateContext();
        var json = JsonSerializer.Serialize(key.EncryptSecrets(new() { ["botToken"] = "token" }));
        var existing = await db.PlatformSettings.SingleOrDefaultAsync(s => s.Category == category);
        if (existing is null)
            db.PlatformSettings.Add(new PlatformSettingsEntity
            {
                Id = Guid.NewGuid(), Category = category, Enabled = enabled, EncryptedJson = json,
            });
        else
            existing.EncryptedJson = json;
        await db.SaveChangesAsync();
    }

    /// <summary>A platform admin who is a member of each tenant, joining them in the order given.</summary>
    private async Task<Guid> SeedPlatformAdminAsync(params Guid[] tenants)
    {
        await using var db = _database.CreateContext();
        var admin = new SubjectEntity { Id = Guid.NewGuid(), Name = "Admin", IsPlatformAdmin = true };
        db.Subjects.Add(admin);
        await db.SaveChangesAsync();
        var joined = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var tenant in tenants)
        {
            db.TenantMembers.Add(new TenantMemberEntity { Id = Guid.NewGuid(), TenantId = tenant, SubjectId = admin.Id });
            await db.SaveChangesAsync();
            await db.TenantMembers.Where(m => m.TenantId == tenant && m.SubjectId == admin.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.SysCreatedAt, joined));
            joined = joined.AddDays(1);
        }
        return admin.Id;
    }

    private string[] OwnersOf(Guid tenantId)
    {
        if (!_owners.TryGetValue(tenantId, out var owners))
            _owners[tenantId] = owners = [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()];
        return owners;
    }

    private static string DisplayName(string connector) =>
        Nocturne.Connectors.Core.Services.ConnectorMetadataService.GetByConnectorId(connector)?.DisplayName ?? connector;

    private static ISecretEncryptionService Encryption(string instanceKey) => new SecretEncryptionService(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ServiceNames.ConfigKeys.InstanceKey] = instanceKey })
            .Build(),
        NullLogger<SecretEncryptionService>.Instance);

    public void Dispose()
    {
        _provider.Dispose();
        _database.Dispose();
    }

    private sealed record Sent(
        Guid TenantId, string UserId, string Type, string SourceId, NotificationUrgency? Urgency,
        List<NotificationActionDto> Actions, Dictionary<string, object> Metadata);

    private sealed class RecordingNotifications(ITenantAccessor tenant, InstanceKeyChangeServiceTests test)
        : IInAppNotificationService
    {
        public Task<InAppNotificationDto> CreateNotificationAsync(
            string userId, string type, string title, NotificationCategory? category = null,
            NotificationUrgency? urgency = null, string? icon = null, string? source = null, string? subtitle = null,
            string? sourceId = null, List<NotificationActionDto>? actions = null,
            ResolutionConditions? resolutionConditions = null, Dictionary<string, object>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            if (userId == test._failFor)
                throw new InvalidOperationException("send failed");
            lock (test._sent)
            {
                test._sent.Add(new Sent(tenant.TenantId, userId, type, sourceId!, urgency, actions ?? [], metadata!));
                test._active.Add(new InAppNotificationEntity
                {
                    Id = Guid.NewGuid(), UserId = userId, Type = type, Source = InstanceKeyChangeService.Source,
                    SourceId = sourceId,
                });
            }
            return Task.FromResult(new InAppNotificationDto());
        }

        public Task<bool> ArchiveNotificationAsync(
            Guid notificationId, NotificationArchiveReason reason, string userId, CancellationToken cancellationToken = default)
        {
            lock (test._sent)
            {
                var notice = test._active.Single(n => n.Id == notificationId && n.UserId == userId);
                test._active.Remove(notice);
                test._archived.Add((userId, notice.Type, notice.SourceId!, reason));
            }
            return Task.FromResult(true);
        }

        public Task<bool> ArchiveBySourceAsync(
            string userId, string type, string sourceId, NotificationArchiveReason reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<InAppNotificationDto>> GetActiveNotificationsAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> GetNotificationTypeAsync(Guid notificationId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExecuteActionAsync(Guid notificationId, string actionId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> MarkAsReadAsync(Guid notificationId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> MarkAllAsReadAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Fails the first context it is asked for, as a database that is not up yet would.</summary>
    private sealed class FailingFirstFactory(IDbContextFactory<NocturneDbContext> inner, Exception failure)
        : IDbContextFactory<NocturneDbContext>
    {
        public bool Failed { get; private set; }

        public NocturneDbContext CreateDbContext()
        {
            if (Failed)
                return inner.CreateDbContext();
            Failed = true;
            throw failure;
        }
    }

    private sealed class ListLogger : ILogger<InstanceKeyChangeService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class StubTenantAccessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;

        public void SetTenant(TenantContext context) => Context = context;
    }
}
