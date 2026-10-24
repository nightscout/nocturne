using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Notifications;
using Nocturne.API.Services.NotificationTemplates;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.V4;

[Trait("Category", "Unit")]
public class RawMmolProfileNotifierTests : IDisposable
{
    private const string Owner = "owner-1";

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OtherTenantId = Guid.CreateVersion7();

    private readonly NocturneDbContext _db = TestDbContextFactory.CreateInMemoryContext();
    private readonly Mock<IRawMmolProfileDetector> _detector = new();
    private readonly Mock<ITenantOwnerResolver> _owners = new();
    private readonly ListLogger<RawMmolProfileNotifier> _log = new();
    private readonly ServiceProvider _services;
    private readonly RawMmolProfileNotifier _notifier;

    public RawMmolProfileNotifierTests()
    {
        _db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "t", DisplayName = "T" });
        _db.SaveChanges();
        _db.TenantId = TenantId;

        _owners.Setup(o => o.GetOwnerSubjectIdAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(Owner);
        Listed(Schedule());

        var contexts = _db.AsFactory();
        _services = new ServiceCollection()
            .AddScoped(_ => Mock.Of<ITenantAccessor>())
            .AddScoped(_ => _detector.Object)
            .AddScoped(_ => _owners.Object)
            .AddScoped<IInAppNotificationRepository>(_ => new InAppNotificationRepository(contexts))
            .AddScoped<IInAppNotificationService>(sp => new InAppNotificationService(
                sp.GetRequiredService<IInAppNotificationRepository>(),
                Mock.Of<ISignalRBroadcastService>(),
                new NotificationTemplateRegistry().AddBuiltInTemplates(),
                [],
                NullLogger<InAppNotificationService>.Instance))
            .BuildServiceProvider();

        _notifier = new RawMmolProfileNotifier(
            _services,
            new ActiveTenantSnapshot(contexts, TimeProvider.System, NullLogger<ActiveTenantSnapshot>.Instance),
            _log);
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    private static RawMmolSchedule Schedule() => new()
    {
        Kind = RawMmolScheduleKind.TargetRange, Id = Guid.CreateVersion7(), ProfileName = "Default",
        Reason = RawMmolReason.TargetInMmolBand,
    };

    private void Listed(params RawMmolSchedule[] schedules) =>
        _detector.Setup(d => d.DetectAsync(It.IsAny<CancellationToken>())).ReturnsAsync(schedules);

    private IEnumerable<string> Errors =>
        _log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => $"{e.Message}: {e.Exception}");

    private List<InAppNotificationEntity> Filed() =>
        _db.InAppNotifications.Where(n => n.Type == RawMmolProfileNotifier.NotificationType).ToList();

    [Fact]
    public async Task ListedSchedules_FileOneNotificationToTheOwner_LinkingToTheProfilePage()
    {
        await _notifier.NotifyAllAsync();

        Errors.Should().BeEmpty();
        var filed = Filed().Should().ContainSingle().Subject;
        filed.UserId.Should().Be(Owner);
        filed.SourceId.Should().Be(TenantId.ToString());
        filed.Title.Should().Be("raw_mmol_profile_values");
        filed.ActionsJson.Should().Contain("\"review\"");
    }

    [Fact]
    public async Task ASecondStart_FilesNothingMore()
    {
        await _notifier.NotifyAllAsync();
        await _notifier.NotifyAllAsync();

        Filed().Should().ContainSingle();
    }

    [Fact]
    public async Task AnArchivedNotificationOfTheTenant_StopsAnother()
    {
        await _notifier.NotifyAllAsync();
        var filed = Filed().Single();
        filed.ReadAt = DateTime.UtcNow;
        filed.IsArchived = true;
        _db.SaveChanges();

        await _notifier.NotifyAllAsync();

        Filed().Should().ContainSingle();
    }

    [Fact]
    public async Task OneOwnerOfTwoTenants_IsToldOncePerTenant()
    {
        _db.Tenants.Add(new TenantEntity { Id = OtherTenantId, Slug = "o", DisplayName = "O" });
        _db.SaveChanges();
        _owners.Setup(o => o.GetOwnerSubjectIdAsync(OtherTenantId, It.IsAny<CancellationToken>())).ReturnsAsync(Owner);

        await _notifier.NotifyAllAsync();
        await _notifier.NotifyAllAsync();

        _db.InAppNotifications.IgnoreQueryFilters()
            .Where(n => n.Type == RawMmolProfileNotifier.NotificationType)
            .Select(n => n.SourceId).ToList().Should().BeEquivalentTo(TenantId.ToString(), OtherTenantId.ToString());
    }

    [Fact]
    public async Task NothingListed_FilesNothing()
    {
        Listed();

        await _notifier.NotifyAllAsync();

        Filed().Should().BeEmpty();
    }

    [Fact]
    public async Task ADetectorThatFails_DoesNotStopStartup()
    {
        _detector.Setup(d => d.DetectAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());

        await _notifier.Invoking(n => n.NotifyAllAsync()).Should().NotThrowAsync();
        Filed().Should().BeEmpty();
        Errors.Should().ContainSingle();
    }
}
