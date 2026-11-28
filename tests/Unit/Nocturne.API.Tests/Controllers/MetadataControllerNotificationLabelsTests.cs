using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

[Trait("Category", "Unit")]
public class MetadataControllerNotificationLabelsTests
{
    [Fact]
    public void GetNotificationLabels_OffersEveryLabelAndEveryType()
    {
        var result = new MetadataController().GetNotificationLabels();

        var metadata = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<NotificationLabelsMetadata>().Subject;
        metadata.AvailableLabels.Should().BeEquivalentTo(Enum.GetValues<NotificationLabel>());
        metadata.Types.Should().BeEquivalentTo(Enum.GetValues<InAppNotificationType>());
    }
}
