using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

[Trait("Category", "Unit")]
public class DeleteResponseTests
{
    private static T WithContext<T>(T controller)
        where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static void AssertStatusEnvelope(ActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        JsonSerializer.Serialize(ok.Value).Should().Be("{\"status\":200}");
    }

    [Fact]
    public async Task DeleteFood_Existing_AnswersStatusEnvelope()
    {
        var foods = new Mock<IFoodRepository>();
        foods.Setup(f => f.DeleteFoodAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = WithContext(
            new FoodController(foods.Object, Mock.Of<IDocumentProcessingService>(), NullLogger<FoodController>.Instance, TimeProvider.System));

        AssertStatusEnvelope(await controller.DeleteFood("abc"));
    }

    [Fact]
    public async Task DeleteProfile_Existing_AnswersStatusEnvelope()
    {
        var writes = new Mock<IProfileWriteService>();
        writes.Setup(w => w.DeleteProfileAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = WithContext(
            new ProfileController(
                Mock.Of<IProfileProjectionService>(),
                writes.Object,
                Mock.Of<IDocumentProcessingService>(),
                NullLogger<ProfileController>.Instance,
                TimeProvider.System));

        AssertStatusEnvelope(await controller.DeleteProfile("abc"));
    }

    [Fact]
    public async Task DeleteSettings_Existing_AnswersStatusEnvelope()
    {
        var settings = new Mock<ISettingsRepository>();
        settings.Setup(s => s.DeleteSettingsAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = WithContext(
            new SettingsController(settings.Object, Mock.Of<IDocumentProcessingService>(), NullLogger<SettingsController>.Instance, TimeProvider.System));

        AssertStatusEnvelope(await controller.DeleteSettings("abc"));
    }
}
