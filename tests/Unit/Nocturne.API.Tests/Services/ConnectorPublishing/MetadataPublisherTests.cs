using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

[Trait("Category", "Unit")]
public class MetadataPublisherTests
{
    private readonly Mock<IProfileWriteService> _mockProfileDataService;
    private readonly Mock<IFoodService> _mockFoodService;
    private readonly Mock<IConnectorFoodEntryService> _mockConnectorFoodEntryService;
    private readonly Mock<IActivityService> _mockActivityService;
    private readonly Mock<IStateSpanService> _mockStateSpanService;
    private readonly Mock<INoteRepository> _mockNoteRepository;
    private readonly Mock<ISystemEventRepository> _mockSystemEventRepository;

    public MetadataPublisherTests()
    {
        _mockProfileDataService = new Mock<IProfileWriteService>();
        _mockFoodService = new Mock<IFoodService>();
        _mockConnectorFoodEntryService = new Mock<IConnectorFoodEntryService>();
        _mockActivityService = new Mock<IActivityService>();
        _mockStateSpanService = new Mock<IStateSpanService>();
        _mockNoteRepository = new Mock<INoteRepository>();
        _mockSystemEventRepository = new Mock<ISystemEventRepository>();
    }

    private MetadataPublisher CreatePublisher()
    {
        return new MetadataPublisher(
            _mockProfileDataService.Object,
            _mockFoodService.Object,
            _mockConnectorFoodEntryService.Object,
            _mockActivityService.Object,
            _mockStateSpanService.Object,
            _mockSystemEventRepository.Object,
            _mockNoteRepository.Object,
            NullLogger<MetadataPublisher>.Instance
        );
    }

    [Fact]
    public async Task PublishProfilesAsync_DelegatesToProfileDataService()
    {
        var profiles = new List<Profile> { new() };

        var publisher = CreatePublisher();
        var result = await publisher.PublishProfilesAsync(profiles, "test-source");

        result.Should().BeTrue();
        _mockProfileDataService.Verify(
            s => s.CreateProfilesAsync(It.IsAny<IEnumerable<Profile>>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PublishFoodAsync_DelegatesToFoodService()
    {
        var foods = new List<Food> { new() };

        var publisher = CreatePublisher();
        var result = await publisher.PublishFoodAsync(foods, "test-source");

        result.Should().BeTrue();
        _mockFoodService.Verify(
            s => s.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PublishConnectorFoodEntriesAsync_DelegatesToConnectorFoodEntryService()
    {
        var entries = new List<ConnectorFoodEntryImport> { new() };

        var publisher = CreatePublisher();
        var result = await publisher.PublishConnectorFoodEntriesAsync(entries, "test-source");

        result.Should().BeTrue();
        _mockConnectorFoodEntryService.Verify(
            s => s.ImportAsync("default", It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PublishActivityAsync_DelegatesToActivityService()
    {
        var activities = new List<Activity> { new() };

        var publisher = CreatePublisher();
        var result = await publisher.PublishActivityAsync(activities, "test-source");

        result.Should().BeTrue();
        _mockActivityService.Verify(
            s => s.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PublishStateSpansAsync_UpsertEachSpanIndividually()
    {
        var spans = new List<StateSpan> { new(), new(), new() };
        var publisher = CreatePublisher();

        var result = await publisher.PublishStateSpansAsync(spans, "test-source");

        result.Should().BeTrue();
        _mockStateSpanService.Verify(
            s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3)
        );
    }

    [Fact]
    public async Task PublishStateSpansAsync_ReturnsFalse_OnException()
    {
        _mockStateSpanService
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));
        var publisher = CreatePublisher();

        var result = await publisher.PublishStateSpansAsync(new List<StateSpan> { new() }, "test-source");

        result.Should().BeFalse();
    }

}
