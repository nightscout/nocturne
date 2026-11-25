namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
public class InAppNotificationRepositoryTests
{
    private const string Malformed = "{not json";

    private static InAppNotificationEntity Entity(
        string? actionsJson = null,
        string? metadataJson = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = "user",
            Type = "test",
            Title = "A title",
            ActionsJson = actionsJson,
            MetadataJson = metadataJson,
        };

    [Fact]
    public void WellFormedColumnsAreReadBack()
    {
        var dto = InAppNotificationRepository.ToDto(Entity(
            actionsJson: """[{"actionId":"dismiss","label":"Dismiss"}]""",
            metadataJson: """{"key":"value"}"""));

        dto.Actions.Should().ContainSingle().Which.ActionId.Should().Be("dismiss");
        dto.Metadata.Should().ContainKey("key");
    }

    [Fact]
    public void MalformedMetadataStillYieldsANoticeWithNullMetadata()
    {
        var dto = InAppNotificationRepository.ToDto(Entity(metadataJson: Malformed));

        dto.Title.Should().Be("A title");
        dto.Metadata.Should().BeNull();
    }

    [Fact]
    public void MalformedActionsStillYieldANoticeWithNoActions()
    {
        var dto = InAppNotificationRepository.ToDto(Entity(actionsJson: Malformed));

        dto.Title.Should().Be("A title");
        dto.Actions.Should().BeEmpty();
    }

    [Fact]
    public void MalformedResolutionConditionsReadAsNone()
    {
        InAppNotificationRepository.DeserializeConditions(Malformed).Should().BeNull();
    }

    [Fact]
    public void MalformedMetadataReadsAsNone()
    {
        InAppNotificationRepository.DeserializeMetadata(Malformed).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyColumnReadsAsNone(string? json)
    {
        InAppNotificationRepository.DeserializeMetadata(json).Should().BeNull();
        InAppNotificationRepository.DeserializeConditions(json).Should().BeNull();
    }
}
