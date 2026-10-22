using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A V3 search with no limit pages by the most Nightscout allows, 1000. A supplied limit lowers
/// it, and one above it is clamped.
/// </summary>
[Trait("Category", "Unit")]
public class V3SearchLimitTests
{
    private static ControllerContext Context(string queryString) => new()
    {
        HttpContext = new DefaultHttpContext { Request = { QueryString = new QueryString(queryString) } },
    };

    [Theory]
    [InlineData("", 1000)]
    [InlineData("?limit=5", 5)]
    [InlineData("?limit=1000", 1000)]
    [InlineData("?limit=5000", 1000)]
    public async Task Entries_AskTheStoreForTheLimit(string queryString, int expected)
    {
        EntryQuery? captured = null;
        var entries = new Mock<IEntryService>();
        entries
            .Setup(s => s.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .Callback<EntryQuery, CancellationToken>((query, _) => captured = query)
            .ReturnsAsync(Array.Empty<Entry>());
        var controller = new EntriesController(
            Mock.Of<IDocumentProcessingService>(),
            entries.Object,
            Mock.Of<ICanonicalAlertEvaluator>(),
            NullLogger<EntriesController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

        await controller.GetEntries();

        captured!.Count.Should().Be(expected);
    }

    [Theory]
    [InlineData("", 1000)]
    [InlineData("?limit=5", 5)]
    [InlineData("?limit=5000", 1000)]
    public async Task Treatments_AskTheStoreForTheLimit(string queryString, int expected)
    {
        TreatmentQuery? captured = null;
        var treatments = new Mock<ITreatmentService>();
        treatments
            .Setup(s => s.QueryTreatmentsAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>()))
            .Callback<TreatmentQuery, CancellationToken>((query, _) => captured = query)
            .ReturnsAsync(Array.Empty<Treatment>());
        var controller = new TreatmentsController(
            Mock.Of<IDocumentProcessingService>(),
            treatments.Object,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

        await controller.GetTreatments();

        captured!.Count.Should().Be(expected);
    }

    [Theory]
    [InlineData("", 1000)]
    [InlineData("?limit=5", 5)]
    [InlineData("?limit=5000", 1000)]
    public async Task Profiles_AskTheStoreForTheLimit(string queryString, int expected)
    {
        var profiles = new Mock<IProfileProjectionService>();
        profiles
            .Setup(s => s.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Profile>());
        var controller = new ProfileController(
            profiles.Object,
            Mock.Of<IProfileWriteService>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<ProfileController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

        await controller.GetProfiles();

        profiles.Verify(
            s => s.GetProfilesAsync(expected, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("", 1000)]
    [InlineData("?limit=5", 5)]
    [InlineData("?limit=5000", 1000)]
    public async Task Settings_AskTheStoreForTheLimit(string queryString, int expected)
    {
        var settings = new Mock<ISettingsRepository>();
        settings
            .Setup(s => s.GetSettingsWithAdvancedFilterAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Settings>());
        var controller = new SettingsController(
            settings.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<SettingsController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

        await controller.GetSettings();

        settings.Verify(
            s => s.GetSettingsWithAdvancedFilterAsync(expected, It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("", 300)]
    [InlineData("?limit=5", 5)]
    [InlineData("?limit=5000", 300)]
    public async Task Food_ReturnsAllMatchesUpToTheLimit(string queryString, int expected)
    {
        var repository = new Mock<IFoodRepository>();
        repository
            .Setup(f => f.GetFoodWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, 300).Select(i => new Food { Name = $"food {i}" }).ToList());
        var controller = new FoodController(
            repository.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<FoodController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

        var result = await controller.GetFood();

        var body = result.Should().BeAssignableTo<ObjectResult>().Subject.Value;
        JsonSerializer.SerializeToNode(body)!["result"]!.AsArray().Should().HaveCount(expected);
    }
}
