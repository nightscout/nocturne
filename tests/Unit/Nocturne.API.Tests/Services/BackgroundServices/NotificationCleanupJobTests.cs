using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Tests.Services.Jobs;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Infrastructure.Data.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

[Trait("Category", "Unit")]
public class NotificationCleanupJobTests
{
    [Fact]
    public async Task A_run_purges_notifications_archived_before_the_retention_cutoff_and_comes_due_a_day_later()
    {
        var now = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var repository = new Mock<IInAppNotificationRepository>();
        repository
            .Setup(r => r.DeleteArchivedBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        await using var services = new ServiceCollection().AddSingleton(repository.Object).BuildServiceProvider();

        var outcome = await new NotificationCleanupJob(NullLogger<NotificationCleanupJob>.Instance)
            .RunAsync(JobRuns.For(Guid.CreateVersion7(), services, now), CancellationToken.None);

        repository.Verify(r => r.DeleteArchivedBeforeAsync(now.UtcDateTime - TimeSpan.FromDays(30), It.IsAny<CancellationToken>()), Times.Once);
        outcome.Should().Be(JobOutcome.Resume(null, now + TimeSpan.FromDays(1)));
    }
}
