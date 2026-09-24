using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// Tenant reads must not be shared-cacheable. <c>UseResponseCaching</c> stores only
/// <c>public</c> responses and answers from its store before authentication runs, so every V3
/// read that sets <c>Cache-Control</c> must mark it <c>private</c>. The <c>max-age</c> stays so
/// clients keep their own cache and conditional GETs.
/// </summary>
[Trait("Category", "Unit")]
public class V3CacheControlTests
{
    private const long Mills = 1711454700000;

    private static ControllerContext Context() => new() { HttpContext = new DefaultHttpContext() };

    private static void ShouldBePrivate(ControllerBase controller)
    {
        var header = controller.Response.Headers.CacheControl.ToString();
        header.Should().NotBeNullOrEmpty();

        var cacheControl = CacheControlHeaderValue.Parse(header);
        cacheControl.Public.Should().BeFalse();
        cacheControl.Private.Should().BeTrue();
        cacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task ProfileCollection_IsNotSharedCacheable()
    {
        var projection = new Mock<IProfileProjectionService>();
        projection
            .Setup(s => s.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Profile { Mills = Mills } });
        projection
            .Setup(s => s.CountProfilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var controller = new ProfileController(
            projection.Object,
            Mock.Of<IProfileWriteService>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<ProfileController>.Instance)
        {
            ControllerContext = Context(),
        };

        var result = await controller.GetProfiles();

        result.Should().BeOfType<OkObjectResult>();
        ShouldBePrivate(controller);
    }

    [Fact]
    public async Task FoodCollection_IsNotSharedCacheable()
    {
        var foods = new Mock<IFoodRepository>();
        foods
            .Setup(f => f.GetFoodWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Food { Name = "apple", CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(Mills).ToString("O") } });

        var controller = new FoodController(
            foods.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<FoodController>.Instance)
        {
            ControllerContext = Context(),
        };

        var result = await controller.GetFood();

        result.Should().BeOfType<OkObjectResult>();
        ShouldBePrivate(controller);
    }

    [Fact]
    public async Task SingleEntry_IsNotSharedCacheable()
    {
        var entries = new Mock<IEntryService>();
        entries
            .Setup(s => s.GetEntryByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Entry { Mills = Mills, Type = "sgv", Sgv = 120 });

        var controller = new EntriesController(
            Mock.Of<IDocumentProcessingService>(),
            entries.Object,
            Mock.Of<ICanonicalAlertEvaluator>(),
            NullLogger<EntriesController>.Instance)
        {
            ControllerContext = Context(),
        };

        var result = await controller.GetEntry("abc");

        result.Result.Should().BeOfType<OkObjectResult>();
        ShouldBePrivate(controller);
    }

    [Fact]
    public async Task SingleTreatment_IsNotSharedCacheable()
    {
        var treatments = new Mock<ITreatmentService>();
        treatments
            .Setup(s => s.GetTreatmentByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Treatment { EventType = "Note", SrvModified = Mills });

        var controller = new TreatmentsController(
            Mock.Of<ITreatmentStore>(),
            Mock.Of<IDocumentProcessingService>(),
            treatments.Object,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = Context(),
        };

        var result = await controller.GetTreatment("abc");

        result.Result.Should().BeOfType<OkObjectResult>();
        ShouldBePrivate(controller);
    }
}
