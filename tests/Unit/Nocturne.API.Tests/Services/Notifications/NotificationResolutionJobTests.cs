using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Notifications;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Tests.Services.Jobs;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Notifications;

[Trait("Category", "Unit")]
public sealed class NotificationResolutionJobTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly Mock<IInAppNotificationRepository> _repository = new();
    private readonly List<(Guid Id, NotificationArchiveReason Reason)> _archived = [];

    public NotificationResolutionJobTests()
    {
        _repository
            .Setup(r => r.ArchiveAsync(It.IsAny<Guid>(), It.IsAny<NotificationArchiveReason>(), It.IsAny<CancellationToken>()))
            .Callback((Guid id, NotificationArchiveReason reason, CancellationToken _) => _archived.Add((id, reason)))
            .ReturnsAsync((InAppNotificationEntity?)null);
    }

    [Fact]
    public async Task A_notification_is_archived_as_expired_from_the_instant_it_expires()
    {
        var beforeExpiry = Notification(Now.UtcDateTime.AddTicks(1));
        var atExpiry = Notification(Now.UtcDateTime);
        var afterExpiry = Notification(Now.UtcDateTime.AddTicks(-1));
        _repository
            .Setup(r => r.GetPendingResolutionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([beforeExpiry, afterExpiry, atExpiry]);

        var outcome = await RunAsync(active: true);

        _archived.Select(a => a.Id).Should().BeEquivalentTo([afterExpiry.Id, atExpiry.Id]);
        _archived.Should().OnlyContain(a => a.Reason == NotificationArchiveReason.Expired);
        outcome.Should().Be(JobOutcome.Resume(null, Now + NotificationResolutionJob.Interval));
    }

    [Fact]
    public async Task An_inactive_tenant_is_parked_without_a_read()
    {
        var outcome = await RunAsync(active: false);

        _repository.VerifyNoOtherCalls();
        outcome.Should().Be(JobOutcome.Resume(null, Now + IScheduledJob.InactiveTenantRecheck));
    }

    private async Task<JobOutcome> RunAsync(bool active)
    {
        await using var services = new ServiceCollection()
            .AddSingleton(_repository.Object)
            .AddSingleton(Mock.Of<ISignalRBroadcastService>())
            .BuildServiceProvider();
        var tenant = new TenantContext(Guid.CreateVersion7(), "t", "T", active, IsDemo: false);
        return await new NotificationResolutionJob(NullLogger<NotificationResolutionJob>.Instance)
            .RunAsync(new JobRun(tenant, null, 0, services, Now), CancellationToken.None);
    }

    private static InAppNotificationEntity Notification(DateTime expiresAt) => new()
    {
        Id = Guid.CreateVersion7(),
        ResolutionConditionsJson = JsonSerializer.Serialize(
            new ResolutionConditions { ExpiresAt = expiresAt },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
    };
}
