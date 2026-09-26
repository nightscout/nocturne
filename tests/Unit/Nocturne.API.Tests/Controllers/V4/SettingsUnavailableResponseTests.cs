using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// A read that cannot reach the tenant's settings is temporary and retryable, so the endpoints
/// that refuse to answer without them say 503 rather than letting the refusal surface as an
/// unhandled 500.
/// </summary>
[Trait("Category", "Unit")]
public class SettingsUnavailableResponseTests
{
    [Fact]
    public async Task GetSuggestion_answers503WhenTheSettingsReadFailed()
    {
        var service = new Mock<ICompressionLowService>();
        service
            .Setup(s => s.GetSuggestionWithEntriesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SettingsUnavailableException("UI settings"));

        var controller = new CompressionLowController(
            service.Object,
            Mock.Of<ICompressionLowDetectionService>()
        );

        (await controller.GetSuggestion(Guid.NewGuid()))
            .Result.Should()
            .BeOfType<ObjectResult>()
            .Which.StatusCode.Should()
            .Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task GetSuggestions_answers503WhenTheSettingsReadFailed()
    {
        var service = new Mock<IMealMatchingService>();
        service
            .Setup(s =>
                s.GetSuggestionsAsync(
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new SettingsUnavailableException("MyFitnessPal matching settings"));

        var controller = new MealMatchingController(
            service.Object,
            Mock.Of<IConnectorFoodEntryRepository>(),
            Mock.Of<IInAppNotificationService>(),
            NullLogger<MealMatchingController>.Instance
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        (await controller.GetSuggestions(null, null))
            .Result.Should()
            .BeOfType<ObjectResult>()
            .Which.StatusCode.Should()
            .Be(StatusCodes.Status503ServiceUnavailable);
    }
}
