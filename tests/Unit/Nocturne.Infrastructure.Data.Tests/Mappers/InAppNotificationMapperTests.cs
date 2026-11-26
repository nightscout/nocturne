using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

[Trait("Category", "Unit")]
public class InAppNotificationMapperTests
{
    private const string Malformed = "{not json";

    private static InAppNotificationEntity Entity(
        string? actionsJson = null,
        string? resolutionConditionsJson = null,
        string? metadataJson = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = "user",
            Type = "test",
            Title = "A title",
            ActionsJson = actionsJson,
            ResolutionConditionsJson = resolutionConditionsJson,
            MetadataJson = metadataJson,
        };

    [Fact]
    public void WellFormedColumnsAreReadBack()
    {
        var notification = InAppNotificationMapper.ToDomain(Entity(
            actionsJson: """[{"actionId":"dismiss","label":"Dismiss"}]""",
            resolutionConditionsJson: """{"expiresAt":"2030-01-02T03:04:05Z"}""",
            metadataJson: """{"key":"value"}"""));

        notification.Actions.Should().ContainSingle().Which.ActionId.Should().Be("dismiss");
        notification.ResolutionConditions!.ExpiresAt.Should().Be(new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        notification.Metadata.Should().ContainKey("key");
    }

    [Fact]
    public void MalformedMetadataStillYieldsANoticeWithNullMetadata()
    {
        var notification = InAppNotificationMapper.ToDomain(Entity(metadataJson: Malformed));

        notification.Title.Should().Be("A title");
        notification.Metadata.Should().BeNull();
    }

    [Fact]
    public void MalformedActionsStillYieldANoticeWithNoActions()
    {
        var notification = InAppNotificationMapper.ToDomain(Entity(actionsJson: Malformed));

        notification.Title.Should().Be("A title");
        notification.Actions.Should().BeEmpty();
    }

    [Fact]
    public void MalformedResolutionConditionsReadAsNone()
    {
        InAppNotificationMapper.ToDomain(Entity(resolutionConditionsJson: Malformed))
            .ResolutionConditions.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyColumnReadsAsNone(string? json)
    {
        var notification = InAppNotificationMapper.ToDomain(
            Entity(actionsJson: json, resolutionConditionsJson: json, metadataJson: json));

        notification.Actions.Should().BeEmpty();
        notification.ResolutionConditions.Should().BeNull();
        notification.Metadata.Should().BeNull();
    }

    [Fact]
    public void ANotificationWithNoActionsOrMetadataStoresNullColumns()
    {
        var entity = InAppNotificationMapper.ToEntity(new InAppNotification { Metadata = [] });

        entity.ActionsJson.Should().BeNull();
        entity.ResolutionConditionsJson.Should().BeNull();
        entity.MetadataJson.Should().BeNull();
    }

    [Fact]
    public void AStoredNotificationReadsBackAsItWasWritten()
    {
        var notification = new InAppNotification
        {
            Id = Guid.CreateVersion7(),
            UserId = "user",
            Type = "test",
            Category = NotificationCategory.ActionRequired,
            Urgency = NotificationUrgency.Warn,
            Icon = "bell",
            Source = "source",
            Title = "A title",
            Subtitle = "A subtitle",
            CreatedAt = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            SourceId = "source-id",
            Actions = [new NotificationActionDto { ActionId = "dismiss", Label = "Dismiss", Variant = "outline" }],
            ResolutionConditions = new ResolutionConditions
            {
                ExpiresAt = new DateTime(2030, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                Glucose = new GlucoseCondition { AboveMgDl = 180, SustainedMinutes = 15 },
            },
            Metadata = new Dictionary<string, object> { ["key"] = "value" },
            ReadAt = new DateTime(2030, 1, 2, 4, 0, 0, DateTimeKind.Utc),
            IsArchived = true,
            ArchivedAt = new DateTime(2030, 1, 2, 5, 0, 0, DateTimeKind.Utc),
            ArchiveReason = NotificationArchiveReason.Dismissed,
        };

        var read = InAppNotificationMapper.ToDomain(InAppNotificationMapper.ToEntity(notification));

        read.Should().BeEquivalentTo(notification, options => options.Excluding(n => n.Metadata));
        read.Metadata!["key"].ToString().Should().Be("value");
    }
}
