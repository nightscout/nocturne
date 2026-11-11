using Nocturne.Tests.Shared.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Profiles;
using Nocturne.Infrastructure.Data;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Npgsql;
using Xunit;
using Nocturne.Core.Contracts.Entries;

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
            Mock.Of<ICompressionLowNightWindowResolver>(),
            Mock.Of<IUISettingsService>(),
            _tenantOwnerResolver.Object,
            tenantAccessor.Object,
            TimeProvider.System,
            NullLogger<CompressionLowService>.Instance);
    }

    /// <summary>
    /// The overnight window comes from the tenant's own bedtime and wake time, so a settings read
    /// that failed leaves nothing to window on: serving entries from the default 23:00-07:00 window
    /// would present another tenant's night as this one's.
    /// </summary>
    [Fact]
    public async Task GetSuggestionWithEntriesAsync_failsRatherThanWindowingOnDefaultSleepHours()
    {
        var suggestion = PendingSuggestion(stillPendingAfterReview: 1);
        var entryService = new Mock<IEntryService>();
        entryService
            .Setup(e => e.GetEntriesAsync(
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Entry>());
        var treatmentService = new Mock<ITreatmentService>();
        treatmentService
            .Setup(t => t.GetTreatmentsAsync(
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Treatment>());

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.TenantId).Returns(TenantId);

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new NocturneDbContext(options) { TenantId = TenantId };
        var uiSettings = new UISettingsService(context.AsFactory(), TimeProvider.System, NullLogger<UISettingsService>.Instance);
        await context.DisposeAsync();

        var sut = new CompressionLowService(
            _repository.Object,
            _stateSpanService.Object,
            entryService.Object,
            treatmentService.Object,
            _notificationService.Object,
            Mock.Of<ICompressionLowNightWindowResolver>(),
            uiSettings,
            _tenantOwnerResolver.Object,
            tenantAccessor.Object,
            TimeProvider.System,
            NullLogger<CompressionLowService>.Instance);

        await sut.Invoking(s => s.GetSuggestionWithEntriesAsync(suggestion.Id))
            .Should()
            .ThrowAsync<SettingsUnavailableException>();
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
    public async Task AcceptSuggestionAsync_DemoSeedTagsTheCreatedExclusionSpan()
    {
        var suggestion = PendingSuggestion(stillPendingAfterReview: 1);
        StateSpan? captured = null;
        _stateSpanService
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .Callback<StateSpan, CancellationToken>((span, _) => captured = span)
            .ReturnsAsync((StateSpan span, CancellationToken _) =>
            {
                span.Id = Guid.NewGuid().ToString();
                return span;
            });

        await _sut.AcceptSuggestionAsync(
            suggestion.Id, suggestion.StartMills, suggestion.EndMills, demoSeed: true);

        captured!.Metadata.Should().ContainKey("DemoSeed").WhoseValue.Should().Be(true);
        captured.Metadata.Should().ContainKey("SuggestionId").WhoseValue.Should().Be(suggestion.Id.ToString());
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

    private CompressionLowSuggestion StoredSuggestion(
        CompressionLowStatus status,
        long startMills = 1_760_000_000_000,
        Guid? stateSpanId = null)
    {
        var suggestion = new CompressionLowSuggestion
        {
            Id = Guid.NewGuid(),
            NightOf = NightOf,
            Status = status,
            StartMills = startMills,
            EndMills = startMills + 900_000,
            StateSpanId = stateSpanId,
        };
        _repository
            .Setup(r => r.GetByIdAsync(suggestion.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(suggestion);
        return suggestion;
    }

    private static CompressionLowBatchItem Item(CompressionLowSuggestion suggestion) =>
        new() { Id = suggestion.Id };

    private void VerifyNothingWritten()
    {
        _repository.Verify(r => r.UpdateAsync(It.IsAny<CompressionLowSuggestion>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _stateSpanService.Verify(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        _stateSpanService.Verify(s => s.DeleteStateSpanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyBatchAsync_WhenOneItemIsNotPending_WritesNothing()
    {
        var pending = StoredSuggestion(CompressionLowStatus.Pending);
        var accepted = StoredSuggestion(CompressionLowStatus.Accepted);

        await _sut.Invoking(s => s.ApplyBatchAsync(
                CompressionLowBatchAction.Dismiss, [Item(pending), Item(accepted)]))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        pending.Status.Should().Be(CompressionLowStatus.Pending);
        VerifyNothingWritten();
    }

    [Theory]
    [InlineData(CompressionLowBatchAction.Accept)]
    [InlineData(CompressionLowBatchAction.Dismiss)]
    public async Task ApplyBatchAsync_WhenAnItemIsNotFound_WritesNothing(
        CompressionLowBatchAction action)
    {
        var own = StoredSuggestion(CompressionLowStatus.Pending);
        var otherTenants = new CompressionLowSuggestion { Id = Guid.NewGuid(), NightOf = NightOf };
        _repository
            .Setup(r => r.GetByIdAsync(otherTenants.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CompressionLowSuggestion?)null);

        await _sut.Invoking(s => s.ApplyBatchAsync(action, [Item(own), Item(otherTenants)]))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        own.Status.Should().Be(CompressionLowStatus.Pending);
        VerifyNothingWritten();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    public async Task ApplyBatchAsync_WhenTheActionIsUnknown_WritesNothing(int action)
    {
        var pending = StoredSuggestion(CompressionLowStatus.Pending);

        await _sut.Invoking(s => s.ApplyBatchAsync((CompressionLowBatchAction)action, [Item(pending)]))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        VerifyNothingWritten();
    }

    [Fact]
    public async Task ApplyBatchAsync_Accept_WritesOneSpanPerSuggestionOnTheBoundsGiven()
    {
        var adjusted = StoredSuggestion(CompressionLowStatus.Pending, startMills: 1_760_000_000_000);
        var detected = StoredSuggestion(CompressionLowStatus.Pending, startMills: 1_760_100_000_000);
        var spans = new List<StateSpan>();
        _stateSpanService
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .Callback<StateSpan, CancellationToken>((span, _) => spans.Add(span))
            .ReturnsAsync((StateSpan span, CancellationToken _) =>
            {
                span.Id = Guid.NewGuid().ToString();
                return span;
            });

        await _sut.ApplyBatchAsync(
            CompressionLowBatchAction.Accept,
            [
                new CompressionLowBatchItem
                {
                    Id = adjusted.Id,
                    StartMills = adjusted.StartMills + 60_000,
                    EndMills = adjusted.EndMills - 60_000,
                },
                Item(detected),
            ]);

        adjusted.Status.Should().Be(CompressionLowStatus.Accepted);
        detected.Status.Should().Be(CompressionLowStatus.Accepted);
        spans.Should().HaveCount(2);
        spans[0].StartTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(adjusted.StartMills + 60_000).UtcDateTime);
        spans[0].EndTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(adjusted.EndMills - 60_000).UtcDateTime);
        spans[1].StartTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(detected.StartMills).UtcDateTime);
        spans[1].EndTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(detected.EndMills).UtcDateTime);
    }

    [Fact]
    public async Task ApplyBatchAsync_SkipsSuggestionsAlreadyInTheStateTheActionLeavesThem()
    {
        var accepted = StoredSuggestion(CompressionLowStatus.Accepted);
        var dismissed = StoredSuggestion(CompressionLowStatus.Dismissed);

        await _sut.ApplyBatchAsync(CompressionLowBatchAction.Accept, [Item(accepted)]);
        await _sut.ApplyBatchAsync(CompressionLowBatchAction.Dismiss, [Item(dismissed)]);

        VerifyNothingWritten();
    }

    [Fact]
    public async Task ApplyBatchAsync_ARetryAfterAMidBatchFailureFinishesTheRest()
    {
        var first = StoredSuggestion(CompressionLowStatus.Pending);
        var second = StoredSuggestion(CompressionLowStatus.Pending);
        var calls = 0;
        _repository
            .Setup(r => r.UpdateAsync(It.IsAny<CompressionLowSuggestion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CompressionLowSuggestion s, CancellationToken _) =>
                ++calls == 2 ? throw new TimeoutException() : s);

        await _sut.Invoking(s => s.ApplyBatchAsync(
                CompressionLowBatchAction.Dismiss, [Item(first), Item(second)]))
            .Should()
            .ThrowAsync<TimeoutException>();
        // The mock hands out one object, so undo the status the write that threw set on it.
        second.Status = CompressionLowStatus.Pending;
        await _sut.ApplyBatchAsync(CompressionLowBatchAction.Dismiss, [Item(first), Item(second)]);

        first.Status.Should().Be(CompressionLowStatus.Dismissed);
        second.Status.Should().Be(CompressionLowStatus.Dismissed);
        _repository.Verify(r => r.UpdateAsync(first, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.UpdateAsync(second, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ApplyBatchAsync_Delete_IgnoresASuggestionThatVanishesBeforeItsTurn()
    {
        var vanishing = StoredSuggestion(CompressionLowStatus.Pending);
        var reads = 0;
        _repository
            .Setup(r => r.GetByIdAsync(vanishing.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++reads == 1 ? vanishing : null);

        await _sut.ApplyBatchAsync(CompressionLowBatchAction.Delete, [Item(vanishing)]);

        VerifyNothingWritten();
    }

    [Fact]
    public async Task ApplyBatchAsync_Delete_RemovesTheSpanOfAnAcceptedSuggestionAndIgnoresOnesAlreadyGone()
    {
        var spanId = Guid.NewGuid();
        var accepted = StoredSuggestion(CompressionLowStatus.Accepted, stateSpanId: spanId);
        var gone = new CompressionLowSuggestion { Id = Guid.NewGuid(), NightOf = NightOf };
        _repository
            .Setup(r => r.GetByIdAsync(gone.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CompressionLowSuggestion?)null);

        await _sut.ApplyBatchAsync(CompressionLowBatchAction.Delete, [Item(accepted), Item(gone)]);

        _stateSpanService.Verify(s => s.DeleteStateSpanAsync(spanId.ToString(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.DeleteAsync(accepted.Id, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.DeleteAsync(gone.Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyBatchAsync_Delete_WhenTheSpanDeleteFails_KeepsTheSuggestionAndThrows()
    {
        var accepted = StoredSuggestion(CompressionLowStatus.Accepted, stateSpanId: Guid.NewGuid());
        _stateSpanService
            .Setup(s => s.DeleteStateSpanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException());

        await _sut.Invoking(s => s.ApplyBatchAsync(CompressionLowBatchAction.Delete, [Item(accepted)]))
            .Should()
            .ThrowAsync<TimeoutException>();

        _repository.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(ArchiveDatabaseFailures))]
    public async Task DismissSuggestionAsync_WhenArchivingTheNotificationFailsInTheDatabase_StillCompletes(
        Exception failure)
    {
        var suggestion = PendingSuggestion(stillPendingAfterReview: 0);
        _notificationService
            .Setup(n => n.ArchiveBySourceAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<NotificationArchiveReason>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        await _sut.DismissSuggestionAsync(suggestion.Id);

        suggestion.Status.Should().Be(CompressionLowStatus.Dismissed);
        _repository.Verify(r => r.UpdateAsync(suggestion, It.IsAny<CancellationToken>()), Times.Once);
    }

    public static TheoryData<Exception> ArchiveDatabaseFailures() =>
        new()
        {
            new DbUpdateException("write failed"),
            new NpgsqlException("connection lost"),
            new RetryLimitExceededException("retries exhausted"),
        };

    [Fact]
    public async Task DismissSuggestionAsync_WhenArchivingTheNotificationIsCancelled_Propagates()
    {
        var suggestion = PendingSuggestion(stillPendingAfterReview: 0);
        _notificationService
            .Setup(n => n.ArchiveBySourceAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<NotificationArchiveReason>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await _sut.Invoking(s => s.DismissSuggestionAsync(suggestion.Id))
            .Should()
            .ThrowAsync<OperationCanceledException>();
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
