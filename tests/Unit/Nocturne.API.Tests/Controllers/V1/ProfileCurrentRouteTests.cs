using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

[Trait("Category", "Unit")]
public class ProfileCurrentRouteTests
{
    [Fact]
    public async Task GetCurrentProfile_WithNoProfile_AnswersTheJsonLiteralNull()
    {
        var projection = new Mock<IProfileProjectionService>();
        projection
            .Setup(p => p.GetCurrentProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Profile?)null);

        var result = await NewController(projection.Object).GetCurrentProfile();

        var content = result.Should().BeOfType<ContentResult>().Which;
        content.Content.Should().Be("null");
        content.ContentType.Should().Be("application/json");
        content.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentProfile_WithAProfile_AnswersTheProfileObject()
    {
        var profile = new Profile { Id = "5f8d0c1e8a7b4c3d9e5f0001", DefaultProfile = "Day" };
        var projection = new Mock<IProfileProjectionService>();
        projection
            .Setup(p => p.GetCurrentProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        var result = await NewController(projection.Object).GetCurrentProfile();

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(profile);
    }

    private static ProfileController NewController(IProfileProjectionService projection) =>
        new(projection, Mock.Of<IProfileWriteService>(), NullLogger<ProfileController>.Instance, TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
}
