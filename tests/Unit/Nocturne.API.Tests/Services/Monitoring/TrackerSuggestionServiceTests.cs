using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

[Trait("Category", "Unit")]
public class TrackerSuggestionServiceTests
{
    private const string UserId = "suggestion-owner";

    private readonly Mock<ITrackerRepository> _trackers = new();
    private readonly Mock<IInAppNotificationRepository> _notifications = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly TrackerSuggestionService _sut;

    private readonly List<(string Action, TrackerInstanceDto Instance)> _broadcasts = [];

    public TrackerSuggestionServiceTests()
    {
        _broadcast
            .Setup(b => b.BroadcastTrackerUpdateAsync(
                It.IsAny<string>(), It.IsAny<TrackerInstanceDto>(), It.IsAny<string>(), It.IsAny<TrackerVisibility>()))
            .Callback((string action, TrackerInstanceDto instance, string _, TrackerVisibility _) =>
                _broadcasts.Add((action, instance)))
            .Returns(Task.CompletedTask);

        _sut = new TrackerSuggestionService(
            _trackers.Object,
            _notifications.Object,
            _broadcast.Object,
            NullLogger<TrackerSuggestionService>.Instance);
    }

    private (TrackerDefinitionEntity Definition, TrackerInstanceEntity Active, InAppNotificationEntity Suggestion)
        GivenSuggestionForActiveTracker()
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            Name = "Infusion Site",
            UserId = UserId,
            LifespanHours = 72,
            Visibility = TrackerVisibility.Public,
        };

        var active = new TrackerInstanceEntity
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            Definition = definition,
            UserId = UserId,
            StartedAt = DateTime.UtcNow.AddHours(-24),
        };

        var suggestion = new InAppNotificationEntity
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Type = "tracker.suggested_match",
            MetadataJson = JsonSerializer.Serialize(new { trackerDefinitionId = definition.Id }),
        };

        _notifications
            .Setup(n => n.GetByIdAsync(suggestion.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(suggestion);

        _trackers
            .Setup(r => r.GetDefinitionByIdAsync(definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);

        _trackers
            .Setup(r => r.GetActiveInstancesForDefinitionAsync(definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([active]);

        _trackers
            .Setup(r => r.CompleteInstanceAsync(
                active.Id, It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, CompletionReason reason, string? notes, string? _, DateTime? _, CancellationToken _) =>
            {
                active.CompletedAt = DateTime.UtcNow;
                active.CompletionReason = reason;
                active.CompletionNotes = notes;
                return active;
            });

        _trackers
            .Setup(r => r.StartInstanceAsync(
                definition.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid definitionId, string userId, string? startNotes, string? _, DateTime? _, DateTime? _,
                CancellationToken _) => new TrackerInstanceEntity
            {
                Id = Guid.NewGuid(),
                DefinitionId = definitionId,
                Definition = definition,
                UserId = userId,
                StartNotes = startNotes,
                StartedAt = DateTime.UtcNow,
            });

        return (definition, active, suggestion);
    }

    [Fact]
    public async Task AcceptSuggestion_BroadcastsTheReplacedInstanceAsComplete()
    {
        var (_, active, suggestion) = GivenSuggestionForActiveTracker();

        var result = await _sut.AcceptSuggestionAsync(suggestion.Id, UserId);

        result.Should().Be(NotificationActionResult.Completed);
        var completion = _broadcasts.Should().ContainSingle(b => b.Instance.Id == active.Id).Subject;
        completion.Action.Should().Be("complete");
        completion.Instance.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AcceptSuggestion_BroadcastsTheStartedInstanceAsCreate()
    {
        var (definition, active, suggestion) = GivenSuggestionForActiveTracker();

        await _sut.AcceptSuggestionAsync(suggestion.Id, UserId);

        _broadcasts.Select(b => b.Action).Should().Equal("complete", "create");
        var started = _broadcasts[1].Instance;
        started.Id.Should().NotBe(active.Id);
        started.DefinitionId.Should().Be(definition.Id);
        started.CompletedAt.Should().BeNull();
    }
}
