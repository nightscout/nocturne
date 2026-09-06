using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// AAPS parses every V3 ETag it reads by stripping the first three characters (<c>W/"</c>)
/// and the trailing quote, then calling <c>toLong()</c>. A non-numeric ETag payload (such as
/// the content-hash ETags V3 collection endpoints used to emit) crashes the AAPS sync loop
/// with <c>NumberFormatException</c> on every cycle (#522).
/// </summary>
[Trait("Category", "Unit")]
public class V3CursorETagTests
{
    private static ProfileController CreateProfileController(
        Mock<IProfileProjectionService> projectionService
    )
    {
        return new ProfileController(
            projectionService.Object,
            Mock.Of<IProfileWriteService>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<ProfileController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
    }

    private static FoodController CreateFoodController(Mock<IFoodRepository> foods)
    {
        return new FoodController(
            foods.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<FoodController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
    }

    [Fact]
    public async Task GetProfiles_SetsAapsParseableCursorETag()
    {
        var mills = new DateTimeOffset(2024, 3, 26, 12, 5, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var projectionService = new Mock<IProfileProjectionService>();
        projectionService
            .Setup(s => s.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Profile { Mills = mills } });
        projectionService
            .Setup(s => s.CountProfilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var controller = CreateProfileController(projectionService);
        await controller.GetProfiles();

        controller.Response.Headers["ETag"].ToString().Should().Be($"W/\"{mills}\"");
    }

    [Fact]
    public async Task GetProfiles_ReturnsEnvelopeDirectly_NotDoubleWrapped()
    {
        var projectionService = new Mock<IProfileProjectionService>();
        projectionService
            .Setup(s => s.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Profile { Mills = 1 } });
        projectionService
            .Setup(s => s.CountProfilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var controller = CreateProfileController(projectionService);
        var result = await controller.GetProfiles();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var envelope = okResult.Value.Should()
            .BeAssignableTo<IDictionary<string, object>>()
            .Subject;
        envelope["status"].Should().Be(200);
        envelope.Should().ContainKey("result");
    }

    [Fact]
    public async Task GetProfiles_IfNoneMatchWithCursorETag_Returns304()
    {
        var mills = new DateTimeOffset(2024, 3, 26, 12, 5, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var projectionService = new Mock<IProfileProjectionService>();
        projectionService
            .Setup(s => s.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Profile { Mills = mills } });
        projectionService
            .Setup(s => s.CountProfilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var controller = CreateProfileController(projectionService);
        controller.HttpContext.Request.Query = new QueryCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["if-none-match"] = $"W/\"{mills}\"",
            }
        );

        var result = await controller.GetProfiles();

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(304);
    }

    [Fact]
    public async Task GetProfileHistory_ReturnsNewerProfilesAscending_AndSetsCursorHeaders()
    {
        var cursor = new DateTimeOffset(2024, 3, 26, 11, 55, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var older = new DateTimeOffset(2024, 3, 26, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var newer = new DateTimeOffset(2024, 3, 26, 12, 5, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var projectionService = new Mock<IProfileProjectionService>();
        projectionService
            .Setup(s => s.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                // Projection returns newest-first
                new Profile { Mills = newer },
                new Profile { Mills = older },
            });

        var controller = CreateProfileController(projectionService);
        var result = await controller.GetProfileHistory(cursor);

        controller.Response.Headers["ETag"].ToString().Should().Be($"W/\"{newer}\"");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var envelope = okResult.Value!;
        var resultProperty = envelope.GetType().GetProperty("result");
        var profiles = resultProperty!.GetValue(envelope)
            .Should().BeAssignableTo<IEnumerable<Profile>>().Subject.ToList();

        // Ascending order matters: AAPS activates the LAST element of the page.
        profiles.Select(p => p.Mills).Should().Equal(older, newer);
    }

    [Fact]
    public async Task GetProfileHistory_EmptyResult_EchoesRequestCursorInETag()
    {
        var cursor = new DateTimeOffset(2024, 3, 26, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var projectionService = new Mock<IProfileProjectionService>();
        projectionService
            .Setup(s => s.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Profile>());

        var controller = CreateProfileController(projectionService);
        await controller.GetProfileHistory(cursor);

        controller.Response.Headers["ETag"].ToString().Should().Be($"W/\"{cursor}\"");
    }

    [Fact]
    public async Task GetFoodHistory_EmptyResult_EchoesRequestCursorInETag()
    {
        var cursor = new DateTimeOffset(2024, 3, 26, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var foods = new Mock<IFoodRepository>();
        foods
            .Setup(f => f.GetFoodWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Food>());

        var controller = CreateFoodController(foods);
        await controller.GetFoodHistory(cursor);

        // AAPS's food loader throws when the ETag is missing, so an empty page must
        // still carry a parseable cursor.
        controller.Response.Headers["ETag"].ToString().Should().Be($"W/\"{cursor}\"");
    }

    [Fact]
    public async Task GetFoodHistory_ReturnsOnlyNewerFoods_AndAdvancesCursor()
    {
        var cursor = new DateTimeOffset(2024, 3, 26, 12, 0, 0, TimeSpan.Zero);
        var newer = cursor.AddMinutes(5);

        var foods = new Mock<IFoodRepository>();
        foods
            .Setup(f => f.GetFoodWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new Food { Name = "old", CreatedAt = cursor.AddMinutes(-5).ToString("O") },
                new Food { Name = "new", CreatedAt = newer.ToString("O") },
            });

        var controller = CreateFoodController(foods);
        var result = await controller.GetFoodHistory(cursor.ToUnixTimeMilliseconds());

        controller.Response.Headers["ETag"].ToString()
            .Should().Be($"W/\"{newer.ToUnixTimeMilliseconds()}\"");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var resultProperty = okResult.Value!.GetType().GetProperty("result");
        var returned = resultProperty!.GetValue(okResult.Value)
            .Should().BeAssignableTo<IEnumerable<Food>>().Subject.ToList();
        returned.Should().ContainSingle().Which.Name.Should().Be("new");
    }

    [Fact]
    public async Task GetTreatment_SingleRecord_SetsAapsParseableCursorETag()
    {
        var mills = new DateTimeOffset(2024, 3, 26, 12, 5, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var treatmentService = new Mock<ITreatmentService>();
        treatmentService
            .Setup(s => s.GetTreatmentByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Treatment { EventType = "Note", SrvModified = mills });

        var controller = new TreatmentsController(
            Mock.Of<ITreatmentStore>(),
            Mock.Of<IDocumentProcessingService>(),
            treatmentService.Object,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        await controller.GetTreatment("abc");

        controller.Response.Headers["ETag"].ToString().Should().Be($"W/\"{mills}\"");
    }
}
