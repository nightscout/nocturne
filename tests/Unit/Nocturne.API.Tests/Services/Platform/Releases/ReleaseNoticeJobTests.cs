using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Notifications;
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
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.API.Tests.Services.Jobs;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Tests.Services.Platform.Releases;

public sealed class ReleaseNoticeJobTests : IDisposable
{
    private static readonly Guid SecondTenant = Guid.NewGuid();

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDatabase _database = TestDbContextFactory.CreateSqlite();
    private readonly Mock<IReleaseSource> _releases = new();
    private readonly Mock<IAutoUpdateProbe> _probe = new();
    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly List<InAppNotificationEntity> _active = [];
    private readonly List<(string UserId, string Type, string Version, Dictionary<string, object> Metadata)> _sent = [];
    private readonly Dictionary<Guid, string[]> _owners = [];
    private readonly HashSet<string> _failingOwners = [];
    private readonly ServiceProvider _provider;

    public ReleaseNoticeJobTests()
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
            .Returns((string userId, string type, string _, NotificationCategory? _, NotificationUrgency? _,
                string? _, string? _, string? _, string? sourceId, List<NotificationActionDto>? _,
                ResolutionConditions? _, Dictionary<string, object>? metadata, CancellationToken _) =>
            {
                if (_failingOwners.Contains(userId))
                    throw new InvalidOperationException("send failed");
                _sent.Add((userId, type, sourceId!, metadata!));
                return Task.FromResult(new InAppNotificationDto());
            });

        var repository = new Mock<IInAppNotificationRepository>();
        repository
            .Setup(r => r.GetActiveBySourceAsync(It.IsAny<string>(), ReleaseNoticeJob.Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string userId, string _, CancellationToken _) => _active.Where(n => n.UserId == userId).ToList());

        var owners = new Mock<ITenantOwnerResolver>();
        owners.Setup(o => o.GetOwnerSubjectIdsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid tenantId, CancellationToken _) => OwnersOf(tenantId));

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

        _sent.Where(s => s.Type == ReleaseNoticeJob.ReleaseInstalledType)
            .Should().OnlyContain(s => s.Version == "0.2.7")
            .And.Subject.Select(s => s.UserId).Should().BeEquivalentTo(owners);
        var available = _sent.Where(s => s.Type == ReleaseNoticeJob.UpdateAvailableType).ToList();
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
    public async Task ARunChecksAndComesDueAgainAfterTheCheckInterval()
    {
        _releases.Setup(r => r.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Release("0.2.8"));
        var job = CreateService("0.2.8", checkOptions: new ReleaseCheckOptions { CheckInterval = TimeSpan.FromHours(3) });

        var outcome = await job.RunAsync(JobRuns.Platform(_provider, _clock.GetUtcNow()), CancellationToken.None);

        outcome.Should().Be(JobOutcome.Resume(null, _clock.GetUtcNow().AddHours(3)));
        _sent.Select(s => s.UserId).Should().BeEquivalentTo(await OwnersAsync());
    }

    [Fact]
    public async Task ADisabledCheckSendsNothingAndStillComesDueAfterTheCheckInterval()
    {
        var job = CreateService("0.2.8", checkOptions: new ReleaseCheckOptions { Enabled = false, CheckInterval = TimeSpan.FromHours(3) });

        var outcome = await job.RunAsync(JobRuns.Platform(_provider, _clock.GetUtcNow()), CancellationToken.None);

        outcome.Should().Be(JobOutcome.Resume(null, _clock.GetUtcNow().AddHours(3)));
        _sent.Should().BeEmpty();
        _releases.VerifyNoOtherCalls();
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
        var owner = OwnersOf(SecondTenant)[0];
        var olderAvailable = Active(owner, ReleaseNoticeJob.UpdateAvailableType, "0.2.8");
        var olderInstalled = Active(owner, ReleaseNoticeJob.ReleaseInstalledType);

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
        var owner = OwnersOf(SecondTenant)[0];
        foreach (var tenant in await TenantsAsync())
        foreach (var subject in OwnersOf(tenant))
        {
            await Ledger().TryClaimAsync(
                ReleaseNoticeJob.InstalledKind, "0.2.7", tenant, Guid.Parse(subject), CancellationToken.None);
        }
        var olderAvailable = Active(owner, ReleaseNoticeJob.UpdateAvailableType, "0.2.8");
        var installed = Active(owner, ReleaseNoticeJob.ReleaseInstalledType);

        await CreateService("0.2.7").CheckAsync(CancellationToken.None);

        _notifications.Verify(n => n.ArchiveNotificationAsync(
            olderAvailable.Id, NotificationArchiveReason.Superseded, owner, It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(n => n.ArchiveNotificationAsync(
            installed.Id, It.IsAny<NotificationArchiveReason>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AFailedSendIsRetriedOnTheNextCheck()
    {
        _releases.Setup(r => r.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Release("0.2.8"));
        var failing = OwnersOf(SecondTenant)[1];
        _failingOwners.Add(failing);
        var service = CreateService("0.2.8");

        await service.CheckAsync(CancellationToken.None);

        _sent.Should().NotContain(s => s.UserId == failing);
        _sent.Select(s => s.UserId).Should().Contain(OwnersOf(SecondTenant)[0], "one owner's failure does not stop the others");

        _failingOwners.Clear();
        _sent.Clear();
        await service.CheckAsync(CancellationToken.None);

        _sent.Should().ContainSingle().Which.UserId.Should().Be(failing);
    }

    [Fact]
    public async Task ATenantCreatedLaterGetsTheCurrentNotices()
    {
        _releases.Setup(r => r.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Release("0.2.8"));
        var service = CreateService("0.2.8");
        await service.CheckAsync(CancellationToken.None);
        _sent.Clear();

        var later = Guid.NewGuid();
        _database.SeedTenant(later, "later");
        await CreateService("0.2.8").CheckAsync(CancellationToken.None);

        _sent.Select(s => s.UserId).Should().BeEquivalentTo(OwnersOf(later));
    }

    [Theory]
    [InlineData(ReleaseNoticeJob.UpdateAvailableType, "0.2.9", ReleaseNoticeJob.UpdateAvailableType, "0.2.8", true)]
    [InlineData(ReleaseNoticeJob.UpdateAvailableType, "0.2.9", ReleaseNoticeJob.ReleaseInstalledType, "0.2.7", false)]
    [InlineData(ReleaseNoticeJob.ReleaseInstalledType, "0.2.8", ReleaseNoticeJob.ReleaseInstalledType, "0.2.7", true)]
    [InlineData(ReleaseNoticeJob.ReleaseInstalledType, "0.2.8", ReleaseNoticeJob.UpdateAvailableType, "0.2.8", true)]
    [InlineData(ReleaseNoticeJob.ReleaseInstalledType, "0.2.8", ReleaseNoticeJob.UpdateAvailableType, "0.2.9", false)]
    public void ANoticeSupersedesWhatItReplaces(string type, string version, string olderType, string olderVersion, bool superseded)
    {
        ReleaseNoticeJob.Supersedes(type, version, olderType, olderVersion).Should().Be(superseded);
    }

    private string[] OwnersOf(Guid tenantId)
    {
        if (!_owners.TryGetValue(tenantId, out var owners))
            _owners[tenantId] = owners = [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()];
        return owners;
    }

    private async Task<IEnumerable<Guid>> TenantsAsync() =>
        (await Snapshot().GetAsync(CancellationToken.None)).Select(t => t.Id);

    private OwnerNoticeLedger Ledger() => new(_provider.GetRequiredService<INocturneDbContextFactory>());

    private InAppNotificationEntity Active(string userId, string type, string? version = null)
    {
        var entity = new InAppNotificationEntity
        {
            Id = Guid.NewGuid(), UserId = userId, Type = type, Source = ReleaseNoticeJob.Source, SourceId = version,
        };
        _active.Add(entity);
        return entity;
    }

    private async Task<IEnumerable<string>> OwnersAsync() =>
        (await TenantsAsync()).SelectMany(OwnersOf);

    private ActiveTenantSnapshot Snapshot() => new(
        new TenantRepository(_provider.GetRequiredService<INocturneDbContextFactory>()), _clock, NullLogger<ActiveTenantSnapshot>.Instance);

    private ReleaseNoticeJob CreateService(string version, bool isRelease = true, ReleaseCheckOptions? checkOptions = null)
    {
        var options = Options.Create(checkOptions ?? new ReleaseCheckOptions());
        var running = new RunningRelease(version, isRelease);
        var status = new ReleaseStatusService(
            _releases.Object, _probe.Object, running, options, _clock, NullLogger<ReleaseStatusService>.Instance);

        return new ReleaseNoticeJob(
            _provider,
            status,
            Ledger(),
            Snapshot(),
            options,
            NullLogger<ReleaseNoticeJob>.Instance);
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
