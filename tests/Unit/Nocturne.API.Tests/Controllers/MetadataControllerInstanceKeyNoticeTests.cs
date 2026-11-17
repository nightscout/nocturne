using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

[Trait("Category", "Unit")]
public class MetadataControllerInstanceKeyNoticeTests
{
    [Fact]
    public void GetInstanceKeyNotice_AnswersWithTheShapesAndNoData()
    {
        var metadata = new MetadataController().GetInstanceKeyNotice().Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<InstanceKeyNoticeMetadata>().Subject;

        metadata.AlertChannel.Should().BeNull();
        metadata.Description.Should().NotBeNullOrWhiteSpace();
    }
}
