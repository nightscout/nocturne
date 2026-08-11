using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Glucose;

[Trait("Category", "Unit")]
public class CompressionLowServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string OwnerSubjectId = "8f0f3d4e-6f2a-4a0b-9d1e-2b3c4d5e6f70";
    private static readonly DateOnly NightOf = new(2026, 4, 17);

    private readonly Mock<ICompressionLowRepository> _repository = new();
    private readonly Mock<IStateSpanService> _stateSpanService = new();
    private readonly Mock<IInAppNotificationService> _notificationService = new();
    private readonly Mock<ITenantOwnerResolver> _tenantOwnerResolver = new();
    private readonly CompressionLowService _sut;

    public CompressionLowServiceTests()
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.TenantId).Returns(TenantId);

        _tenantOwnerResolver
            .Setup(r => r.GetOwnerSubjectIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnerSubjectId);

        _stateSpanService
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan span, CancellationToken _) =>
            {
                span.Id = Guid.NewGuid().ToString();
                return span;
            });

        _sut = new CompressionLowService(
            _repository.Object,
            _stateSpanService.Object,
            Mock.Of<IEntryService>(),
            Mock.Of<ITreatmentService>(),
            _notificationService.Object,
            Mock.Of<ITherapySettingsResolver>(),
            Mock.Of<IUISettingsService>(),
            _tenantOwnerResolver.Object,
            tenantAccessor.Object,
            NullLogger<CompressionLowService>.Instance);
    }

    private CompressionLowSuggestion PendingSuggestion(int stillPendingAfterReview)
    {
        var suggestion = new CompressionLowSuggestion
        {
            Id = Guid.NewGuid(),
            NightOf = NightOf,
            Status = CompressionLowStatus.Pending,
            StartMills = 1_760_000_000_000,
            EndMills = 1_760_000_900_000
        };

        _repository
            .Setup(r => r.GetByIdAsync(suggestion.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(suggestion);
        _repository
            .Setup(r => r.CountPendingForNightAsync(NightOf, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stillPendingAfterReview);

        return suggestion;
    }

    private void VerifyArchivedUnder(string userId) =>
        _notificationService.Verify(n => n.ArchiveBySourceAsync(
            userId,
            CompressionLowDetectionService.NotificationType,
            "2026-04-17",
            NotificationArchiveReason.Completed,
            It.IsAny<CancellationToken>()), Times.Once);

    [Fact]
    public async Task AcceptSuggestionAsync_WhenNightFullyReviewed_ArchivesNotificationUnderTenantOwner()
    {
        var suggestion = PendingSuggestion(stillPendingAfterReview: 0);

        await _sut.AcceptSuggestionAsync(suggestion.Id, suggestion.StartMills, suggestion.EndMills);

        VerifyArchivedUnder(OwnerSubjectId);
    }

    [Fact]
    public async Task DismissSuggestionAsync_WhenNightFullyReviewed_ArchivesNotificationUnderTenantOwner()
    {
        var suggestion = PendingSuggestion(stillPendingAfterReview: 0);

        await _sut.DismissSuggestionAsync(suggestion.Id);

        suggestion.Status.Should().Be(CompressionLowStatus.Dismissed);
        VerifyArchivedUnder(OwnerSubjectId);
    }

    [Fact]
    public async Task DismissSuggestionAsync_WhenSuggestionsStillPending_LeavesNotificationActive()
    {
        var suggestion = PendingSuggestion(stillPendingAfterReview: 1);

        await _sut.DismissSuggestionAsync(suggestion.Id);

        _notificationService.Verify(n => n.ArchiveBySourceAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<NotificationArchiveReason>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DismissSuggestionAsync_WhenTenantHasNoOwner_LeavesNotificationActive()
    {
        _tenantOwnerResolver
            .Setup(r => r.GetOwnerSubjectIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var suggestion = PendingSuggestion(stillPendingAfterReview: 0);

        await _sut.DismissSuggestionAsync(suggestion.Id);

        suggestion.Status.Should().Be(CompressionLowStatus.Dismissed);
        _notificationService.Verify(n => n.ArchiveBySourceAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<NotificationArchiveReason>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
