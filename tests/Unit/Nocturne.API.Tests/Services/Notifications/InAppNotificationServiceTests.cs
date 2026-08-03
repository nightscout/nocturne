using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Notifications;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Services.Notifications;

/// <summary>
/// Unit tests for the per-subject bookkeeping on <see cref="InAppNotificationService"/>: the
/// single mark-read ownership/no-op guards, bulk mark-all-read broadcasting, the archive
/// ownership guard that confines <c>DELETE /api/v4/notifications/{id}</c> to the caller's own rows,
/// and the type accessor that <c>NotificationsController.ExecuteAction</c> authorizes on.
/// </summary>
public class InAppNotificationServiceTests
{
    private readonly Mock<IInAppNotificationRepository> _repository = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly InAppNotificationService _service;

    public InAppNotificationServiceTests()
    {
        _service = new InAppNotificationService(
            _repository.Object,
            _broadcast.Object,
            Mock.Of<INotificationTemplateRegistry>(),
            Array.Empty<INotificationActionHandler>(),
            Mock.Of<ILogger<InAppNotificationService>>()
        );
    }

    private static InAppNotificationEntity Notification(
        string userId,
        DateTime? readAt = null
    ) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Type = "glucose.compression_low_review",
        Category = NotificationCategory.ActionRequired,
        Urgency = NotificationUrgency.Info,
        Title = "compression_low_detected",
        ReadAt = readAt,
    };

    [Fact]
    public async Task MarkAsReadAsync_WhenNotFound_ReturnsFalseAndDoesNotBroadcast()
    {
        _repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InAppNotificationEntity?)null);

        var result = await _service.MarkAsReadAsync(Guid.NewGuid(), "user-1");

        Assert.False(result);
        _repository.Verify(
            r => r.MarkAsReadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _broadcast.Verify(
            b => b.BroadcastNotificationUpdatedAsync(It.IsAny<string>(), It.IsAny<InAppNotificationDto>()),
            Times.Never);
    }

    [Fact]
    public async Task MarkAsReadAsync_WhenOwnedByAnotherUser_ReturnsFalse()
    {
        var entity = Notification("owner");
        _repository
            .Setup(r => r.GetByIdAsync(entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var result = await _service.MarkAsReadAsync(entity.Id, "someone-else");

        Assert.False(result);
        _repository.Verify(
            r => r.MarkAsReadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MarkAsReadAsync_WhenAlreadyRead_ReturnsTrueWithoutReWriting()
    {
        var entity = Notification("user-1", readAt: DateTime.UtcNow);
        _repository
            .Setup(r => r.GetByIdAsync(entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var result = await _service.MarkAsReadAsync(entity.Id, "user-1");

        Assert.True(result);
        _repository.Verify(
            r => r.MarkAsReadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _broadcast.Verify(
            b => b.BroadcastNotificationUpdatedAsync(It.IsAny<string>(), It.IsAny<InAppNotificationDto>()),
            Times.Never);
    }

    [Fact]
    public async Task MarkAsReadAsync_WhenUnread_MarksReadAndBroadcasts()
    {
        var entity = Notification("user-1");
        _repository
            .Setup(r => r.GetByIdAsync(entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        _repository
            .Setup(r => r.MarkAsReadAsync(entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Notification("user-1", readAt: DateTime.UtcNow));

        var result = await _service.MarkAsReadAsync(entity.Id, "user-1");

        Assert.True(result);
        _repository.Verify(
            r => r.MarkAsReadAsync(entity.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        _broadcast.Verify(
            b => b.BroadcastNotificationUpdatedAsync("user-1", It.IsAny<InAppNotificationDto>()),
            Times.Once);
    }

    [Fact]
    public async Task MarkAllAsReadAsync_BroadcastsEachUpdatedNotificationAndReturnsCount()
    {
        var updated = new List<InAppNotificationEntity>
        {
            Notification("user-1", readAt: DateTime.UtcNow),
            Notification("user-1", readAt: DateTime.UtcNow),
        };
        _repository
            .Setup(r => r.MarkAllAsReadAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);

        var count = await _service.MarkAllAsReadAsync("user-1");

        Assert.Equal(2, count);
        _broadcast.Verify(
            b => b.BroadcastNotificationUpdatedAsync("user-1", It.IsAny<InAppNotificationDto>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task MarkAllAsReadAsync_WhenNothingUnread_ReturnsZeroAndDoesNotBroadcast()
    {
        _repository
            .Setup(r => r.MarkAllAsReadAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InAppNotificationEntity>());

        var count = await _service.MarkAllAsReadAsync("user-1");

        Assert.Equal(0, count);
        _broadcast.Verify(
            b => b.BroadcastNotificationUpdatedAsync(It.IsAny<string>(), It.IsAny<InAppNotificationDto>()),
            Times.Never);
    }

    /// <summary>
    /// The archive path resolved by notification id alone before this guard, so any authenticated
    /// subject could clear another member's notification by forwarding its id — the same forged-id
    /// hole <c>MarkAsReadAsync</c> and <c>ExecuteActionAsync</c> already closed.
    /// </summary>
    [Fact]
    public async Task ArchiveNotificationAsync_WhenOwnedByAnotherUser_ReturnsFalseAndDoesNotArchive()
    {
        var entity = Notification("owner");
        _repository
            .Setup(r => r.GetByIdAsync(entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var result = await _service.ArchiveNotificationAsync(
            entity.Id, NotificationArchiveReason.Dismissed, "someone-else");

        Assert.False(result);
        _repository.Verify(
            r => r.ArchiveAsync(
                It.IsAny<Guid>(), It.IsAny<NotificationArchiveReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _broadcast.Verify(
            b => b.BroadcastNotificationArchivedAsync(
                It.IsAny<string>(),
                It.IsAny<InAppNotificationDto>(),
                It.IsAny<NotificationArchiveReason>()),
            Times.Never);
    }

    [Fact]
    public async Task ArchiveNotificationAsync_WhenNotFound_ReturnsFalseAndDoesNotArchive()
    {
        _repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InAppNotificationEntity?)null);

        var result = await _service.ArchiveNotificationAsync(
            Guid.NewGuid(), NotificationArchiveReason.Dismissed, "user-1");

        Assert.False(result);
        _repository.Verify(
            r => r.ArchiveAsync(
                It.IsAny<Guid>(), It.IsAny<NotificationArchiveReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// <c>NotificationsController.ExecuteAction</c> authorizes on the type before dispatching when
    /// the caller was admitted by <c>device.notify</c> alone, so this accessor must be subject to
    /// the same ownership confinement as the writes — otherwise it reports the type of another
    /// member's notification.
    /// </summary>
    [Fact]
    public async Task GetNotificationTypeAsync_WhenOwnedByTheCaller_ReturnsTheType()
    {
        var entity = Notification("user-1");
        _repository
            .Setup(r => r.GetByIdAsync(entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var type = await _service.GetNotificationTypeAsync(entity.Id, "user-1");

        Assert.Equal(entity.Type, type);
    }

    [Fact]
    public async Task GetNotificationTypeAsync_WhenOwnedByAnotherUser_ReturnsNull()
    {
        var entity = Notification("owner");
        _repository
            .Setup(r => r.GetByIdAsync(entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        Assert.Null(await _service.GetNotificationTypeAsync(entity.Id, "someone-else"));
    }

    [Fact]
    public async Task GetNotificationTypeAsync_WhenNotFound_ReturnsNull()
    {
        _repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InAppNotificationEntity?)null);

        Assert.Null(await _service.GetNotificationTypeAsync(Guid.NewGuid(), "user-1"));
    }

    [Fact]
    public async Task ArchiveNotificationAsync_WhenOwnedByTheCaller_ArchivesAndBroadcasts()
    {
        var entity = Notification("user-1");
        _repository
            .Setup(r => r.GetByIdAsync(entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        _repository
            .Setup(r => r.ArchiveAsync(
                entity.Id, NotificationArchiveReason.Dismissed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var result = await _service.ArchiveNotificationAsync(
            entity.Id, NotificationArchiveReason.Dismissed, "user-1");

        Assert.True(result);
        _repository.Verify(
            r => r.ArchiveAsync(
                entity.Id, NotificationArchiveReason.Dismissed, It.IsAny<CancellationToken>()),
            Times.Once);
        _broadcast.Verify(
            b => b.BroadcastNotificationArchivedAsync(
                "user-1", It.IsAny<InAppNotificationDto>(), NotificationArchiveReason.Dismissed),
            Times.Once);
    }
}
