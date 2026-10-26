using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Platform.Releases;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using static Nocturne.API.Tests.Services.Platform.Releases.ReleaseStatusServiceTests;

namespace Nocturne.API.Tests.Services.Platform.Releases;

public sealed class ReleaseNoticeServiceTests : IDisposable
{
    private static readonly Guid SecondTenant = Guid.NewGuid();

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDatabase _database = TestDbContextFactory.CreateSqlite();
    private readonly Mock<IReleaseSource> _releases = new();
    private readonly Mock<IAutoUpdateProbe> _probe = new();
    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly List<InAppNotificationEntity> _active = [];
    private readonly List<(string UserId, string Type, string Version, Dictionary<string, object> Metadata)> _sent = [];
    private readonly ServiceProvider _provider;

    public ReleaseNoticeServiceTests()
    {
        _database.SeedTenant(SecondTenant, "second");

        _probe.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AutoUpdateStatusDto
        {
            Mode = AutoUpdateMode.Automatic,
            CheckIntervalSeconds = 86400,
            NextCheckAt = _clock.GetUtcNow().AddHours(9),
        });
        _notifications
            .Setup(n => n.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Callback((string userId, string type, string _, NotificationCategory? _, NotificationUrgency? _,
                    string? _, string? _, string? _, string? sourceId, List<NotificationActionDto>? _,
                    ResolutionConditions? _, Dictionary<string, object>? metadata, CancellationToken _) =>
                _sent.Add((userId, type, sourceId!, metadata!)))
            .ReturnsAsync(new InAppNotificationDto());

        var repository = new Mock<IInAppNotificationRepository>();
        repository
            .Setup(r => r.GetActiveBySourceAsync(It.IsAny<string>(), ReleaseNoticeService.Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string userId, string _, CancellationToken _) => _active.Where(n => n.UserId == userId).ToList());

        var owners = new Mock<ITenantOwnerResolver>();
        owners.Setup(o => o.GetOwnerSubjectIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid tenantId, CancellationToken _) => $"owner-{tenantId}");

        var services = new ServiceCollection();
        _database.AddToServices(services);
        services.AddScoped<ITenantAccessor, StubTenantAccessor>();
        services.AddScoped(_ => owners.Object);
        services.AddScoped(_ => _notifications.Object);
        services.AddScoped(_ => repository.Object);
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task EachOwnerHearsOnceAboutTheReleaseRunningAndTheOneAvailable()
    {
        _releases.Setup(r => r.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Release("0.2.8"));
        _releases.Setup(r => r.GetByVersionAsync(It.IsAny<ReleaseVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Release("0.2.7"));
        var service = CreateService("0.2.7");
        var owners = await OwnersAsync();

        await service.CheckAsync(CancellationToken.None);

        _sent.Where(s => s.Type == ReleaseNoticeService.ReleaseInstalledType)
            .Should().OnlyContain(s => s.Version == "0.2.7")
            .And.Subject.Select(s => s.UserId).Should().BeEquivalentTo(owners);
        var available = _sent.Where(s => s.Type == ReleaseNoticeService.UpdateAvailableType).ToList();
        available.Select(s => s.UserId).Should().BeEquivalentTo(owners);
        available.Should().OnlyContain(s => s.Version == "0.2.8"
            && (string)s.Metadata["AutoUpdateMode"] == "Automatic"
            && (string)s.Metadata["NextCheckAt"] == _clock.GetUtcNow().AddHours(9).ToString("O"));

        _sent.Clear();
        await service.CheckAsync(CancellationToken.None);
        await CreateService("0.2.7").CheckAsync(CancellationToken.None);

        _sent.Should().BeEmpty("the ledger, not the notifications, records what was sent");
    }

    [Fact]
    public async Task ABuildThatIsNotAReleaseSendsNothing()
    {
        await CreateService("1.0.0", isRelease: false).CheckAsync(CancellationToken.None);

        _sent.Should().BeEmpty();
        _releases.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TheInstalledNoticeSupersedesEveryOlderReleaseNotice()
    {
        _releases.Setup(r => r.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Release("0.2.8"));
        var owner = $"owner-{SecondTenant}";
        var olderAvailable = Active(owner, ReleaseNoticeService.UpdateAvailableType);
        var olderInstalled = Active(owner, ReleaseNoticeService.ReleaseInstalledType);

        await CreateService("0.2.8").CheckAsync(CancellationToken.None);

        _notifications.Verify(n => n.ArchiveNotificationAsync(
            olderAvailable.Id, NotificationArchiveReason.Superseded, owner, It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(n => n.ArchiveNotificationAsync(
            olderInstalled.Id, NotificationArchiveReason.Superseded, owner, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnAvailableNoticeSupersedesOnlyOlderAvailableNotices()
    {
        _releases.Setup(r => r.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Release("0.2.9"));
        await new ReleaseNoticeLedger(_provider.GetRequiredService<INocturneDbContextFactory>())
            .TryClaimAsync(ReleaseNoticeLedger.Installed, "0.2.7", CancellationToken.None);
        var owner = $"owner-{SecondTenant}";
        var olderAvailable = Active(owner, ReleaseNoticeService.UpdateAvailableType);
        var installed = Active(owner, ReleaseNoticeService.ReleaseInstalledType);

        await CreateService("0.2.7").CheckAsync(CancellationToken.None);

        _notifications.Verify(n => n.ArchiveNotificationAsync(
            olderAvailable.Id, NotificationArchiveReason.Superseded, owner, It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(n => n.ArchiveNotificationAsync(
            installed.Id, It.IsAny<NotificationArchiveReason>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private InAppNotificationEntity Active(string userId, string type)
    {
        var entity = new InAppNotificationEntity
        {
            Id = Guid.NewGuid(), UserId = userId, Type = type, Source = ReleaseNoticeService.Source,
        };
        _active.Add(entity);
        return entity;
    }

    private async Task<IEnumerable<string>> OwnersAsync() =>
        (await Snapshot().GetAsync(CancellationToken.None)).Select(t => $"owner-{t.Id}");

    private ActiveTenantSnapshot Snapshot() => new(
        _provider.GetRequiredService<INocturneDbContextFactory>(), _clock, NullLogger<ActiveTenantSnapshot>.Instance);

    private ReleaseNoticeService CreateService(string version, bool isRelease = true)
    {
        var options = Options.Create(new ReleaseCheckOptions());
        var running = new RunningRelease(version, isRelease);
        var status = new ReleaseStatusService(
            _releases.Object, _probe.Object, running, options, _clock, NullLogger<ReleaseStatusService>.Instance);

        return new ReleaseNoticeService(
            _provider,
            status,
            new ReleaseNoticeLedger(_provider.GetRequiredService<INocturneDbContextFactory>()),
            Snapshot(),
            options,
            _clock,
            NullLogger<ReleaseNoticeService>.Instance);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _database.Dispose();
    }

    private sealed class StubTenantAccessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;

        public void SetTenant(TenantContext context) => Context = context;
    }
}
