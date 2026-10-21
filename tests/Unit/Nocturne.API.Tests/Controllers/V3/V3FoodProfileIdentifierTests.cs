using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A v3 client keys every document on <c>identifier</c>: Nightscout sets it from <c>_id</c> on search,
/// read and history, and the by-id routes address the document by it.
/// </summary>
[Trait("Category", "Unit")]
public class V3FoodProfileIdentifierTests
{
    private const string ObjectId = "5f1d7a3b9c0e4a2b8d6c1e0f";
    private const string RowIdentifier = "0197a1b2c3d47e5f8a6b7c8d";
    private static readonly Guid RowId = Guid.Parse("0197a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b");
    private static readonly JsonSerializerOptions WireOptions = NightscoutJsonOptions.Create();

    private readonly Mock<IFoodRepository> _foods = new();
    private readonly Mock<IProfileProjectionService> _profiles = new();

    private FoodController FoodController() =>
        new(_foods.Object, Mock.Of<IDocumentProcessingService>(), NullLogger<FoodController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private ProfileController ProfileController() =>
        new(
            _profiles.Object,
            Mock.Of<IProfileWriteService>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<ProfileController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static JsonElement Wire(ActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(ok.Value, WireOptions);
    }

    private static Food Food(string? id, string name = "apple") => new()
    {
        Id = id,
        Name = name,
        Carbs = 14,
        CreatedAt = "2026-03-10T12:00:00.000Z",
        SrvModified = 1773144000000,
        SrvCreated = 1773144000000,
    };

    private static Profile Profile(string id) => new() { Id = id, Mills = 1773144000000 };

    private void SetupFoodSearch(params Food[] foods) =>
        _foods
            .Setup(f => f.GetFoodWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(foods);

    [Fact]
    public async Task FoodSearch_ServesTheIdentifierAndTheServerStamps()
    {
        SetupFoodSearch(Food(ObjectId));

        var document = Wire(await FoodController().GetFood()).GetProperty("result")[0];

        document.GetProperty("identifier").GetString().Should().Be(ObjectId);
        document.GetProperty("_id").GetString().Should().Be(ObjectId);
        document.GetProperty("srvModified").GetInt64().Should().Be(1773144000000);
        document.GetProperty("srvCreated").GetInt64().Should().Be(1773144000000);
    }

    [Fact]
    public async Task FoodSearch_ServesAUuidKeyedFoodUnderTheObjectIdAReadResolves()
    {
        SetupFoodSearch(Food(RowId.ToString()));

        var document = Wire(await FoodController().GetFood()).GetProperty("result")[0];

        document.GetProperty("identifier").GetString().Should().Be(RowIdentifier);
    }

    [Fact]
    public async Task FoodHistory_ServesTheIdentifier()
    {
        _foods
            .Setup(f => f.GetFoodModifiedSinceAsync(0, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModifiedSincePage<Food>([Food(RowId.ToString())], 1773144000000));

        var document = Wire(await FoodController().GetFoodHistory(0)).GetProperty("result")[0];

        document.GetProperty("identifier").GetString().Should().Be(RowIdentifier);
    }

    [Fact]
    public async Task FoodRead_ServesTheIdentifierTheFoodWasFoundBy()
    {
        _foods
            .Setup(f => f.GetFoodByIdAsync(RowIdentifier, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Food(RowId.ToString()));

        var document = Wire(await FoodController().GetFoodById(RowIdentifier));

        document.GetProperty("identifier").GetString().Should().Be(RowIdentifier);
        document.GetProperty("name").GetString().Should().Be("apple");
    }

    [Fact]
    public async Task FoodSearch_WithFieldSelection_CanSelectTheIdentifier()
    {
        SetupFoodSearch(Food(ObjectId));
        var controller = FoodController();
        controller.HttpContext.Request.QueryString = new QueryString("?fields=identifier,name");

        var document = Wire(await controller.GetFood()).GetProperty("result")[0];

        document.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("identifier", "name");
    }

    [Fact]
    public async Task ProfileSearch_ServesTheIdentifier()
    {
        _profiles
            .Setup(p => p.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Profile(ObjectId)]);
        _profiles
            .Setup(p => p.CountProfilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var document = Wire(await ProfileController().GetProfiles()).GetProperty("result")[0];

        document.GetProperty("identifier").GetString().Should().Be(ObjectId);
        document.GetProperty("_id").GetString().Should().Be(ObjectId);
    }

    [Fact]
    public async Task ProfileHistory_ServesTheIdentifier()
    {
        _profiles
            .Setup(p => p.GetProfilesModifiedSinceAsync(0, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModifiedSincePage<Profile>([Profile(RowId.ToString())], 1773144000000));

        var document = Wire(await ProfileController().GetProfileHistory(0)).GetProperty("result")[0];

        document.GetProperty("identifier").GetString().Should().Be(RowIdentifier);
    }

    [Fact]
    public async Task ProfileRead_ServesTheIdentifier()
    {
        _profiles
            .Setup(p => p.GetProfileByIdAsync(ObjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(ObjectId));

        var document = Wire(await ProfileController().GetProfileById(ObjectId));

        document.GetProperty("identifier").GetString().Should().Be(ObjectId);
    }

    [Fact]
    public void LegacyFoodAndProfileShapes_CarryNoIdentifier()
    {
        JsonSerializer.SerializeToElement(Food(ObjectId), WireOptions)
            .TryGetProperty("identifier", out _).Should().BeFalse();
        JsonSerializer.SerializeToElement(Profile(ObjectId), WireOptions)
            .TryGetProperty("identifier", out _).Should().BeFalse();
    }
}
