using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

[Trait("Category", "Unit")]
public class V3FoodRegexFilterTests
{
    private static readonly Food[] Foods =
    [
        new() { Id = "1", Name = "banana" },
        new() { Id = "2", Name = "Bread" },
        new() { Id = "3", Name = new string('a', 30) },
    ];

    [Theory]
    [InlineData("^ban", "banana")]
    [InlineData("^b", "banana")]
    [InlineData("^[Bb]", "banana,Bread")]
    public async Task Re_MatchesOrdinaryPatterns(string pattern, string expected)
    {
        var names = await NamesFor(pattern);

        names.Should().BeEquivalentTo(expected.Split(','));
    }

    [Fact]
    public async Task Re_NestedAlternation_MatchesInLinearTime()
    {
        var stopwatch = Stopwatch.StartNew();
        var names = await NamesFor("^(.|.)*z$");

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        names.Should().BeEmpty();
    }

    [Fact]
    public async Task Re_PatternTheEngineCannotRun_MatchesNothing()
    {
        var names = await NamesFor(@"(a)\1");

        names.Should().BeEmpty();
    }

    private static async Task<string[]> NamesFor(string pattern)
    {
        var foods = new Mock<IFoodRepository>();
        foods
            .Setup(f => f.GetFoodWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Foods);
        var controller = new FoodController(
            foods.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<FoodController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { QueryString = QueryString.Create("name$re", pattern) },
                },
            },
        };

        var result = await controller.GetFood();

        var body = JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Which.Value);
        return body.GetProperty("result").EnumerateArray()
            .Select(food => food.GetProperty("name").GetString()!)
            .ToArray();
    }
}
