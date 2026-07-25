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
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Xunit;
using Nocturne.Core.Contracts.V4;

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
    private readonly Mock<ITenantOwnerResolver> _mockTenantOwnerResolver;
    private readonly Mock<ITenantAccessor> _mockTenantAccessor;

    private static readonly Guid TenantId = Guid.NewGuid();
    private const string OwnerSubjectId = "0199aaaa-bbbb-cccc-dddd-eeeeffff0000";

    public MetadataPublisherTests()
    {
        _mockProfileDataService = new Mock<IProfileWriteService>();
        _mockFoodService = new Mock<IFoodService>();
        _mockConnectorFoodEntryService = new Mock<IConnectorFoodEntryService>();
        _mockActivityService = new Mock<IActivityService>();
        _mockStateSpanService = new Mock<IStateSpanService>();
        _mockNoteRepository = new Mock<INoteRepository>();
        _mockSystemEventRepository = new Mock<ISystemEventRepository>();

        _mockTenantAccessor = new Mock<ITenantAccessor>();
        _mockTenantAccessor.Setup(t => t.IsResolved).Returns(true);
        _mockTenantAccessor.Setup(t => t.TenantId).Returns(TenantId);

        _mockTenantOwnerResolver = new Mock<ITenantOwnerResolver>();
        _mockTenantOwnerResolver
            .Setup(r => r.GetOwnerSubjectIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnerSubjectId);
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
            _mockTenantOwnerResolver.Object,
            _mockTenantAccessor.Object,
            NullLogger<MetadataPublisher>.Instance
        );
    }

    [Fact]
    public async Task PublishConnectorFoodEntriesAsync_AttributesEntriesToTheTenantOwner()
    {
        // Notifications are keyed by subject id and the UI lists them for the signed-in subject, so
        // a connector filing them under anything else raises suggestions nobody can see.
        await CreatePublisher().PublishConnectorFoodEntriesAsync(
            [new ConnectorFoodEntryImport()], "myfitnesspal-connector", WriteOrigin.Live);

        _mockConnectorFoodEntryService.Verify(
            s => s.ImportAsync(
                OwnerSubjectId,
                It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReconcileConnectorFoodEntriesAsync_AttributesWithdrawalsToTheTenantOwner()
    {
        await CreatePublisher().ReconcileConnectorFoodEntriesAsync(
            ["entry-1"], DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow,
            "myfitnesspal-connector", WriteOrigin.Live);

        _mockConnectorFoodEntryService.Verify(
            s => s.MarkMissingAsDeletedAsync(
                OwnerSubjectId,
                "myfitnesspal-connector",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishConnectorFoodEntriesAsync_StillImportsWhenTheTenantHasNoOwner()
    {
        _mockTenantOwnerResolver
            .Setup(r => r.GetOwnerSubjectIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await CreatePublisher().PublishConnectorFoodEntriesAsync(
            [new ConnectorFoodEntryImport()], "myfitnesspal-connector", WriteOrigin.Live);

        // The data still lands; only the suggestions it would have raised are skipped.
        _mockConnectorFoodEntryService.Verify(
            s => s.ImportAsync(
                null,
                It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishProfilesAsync_DelegatesToProfileDataService()
    {
        var profiles = new List<Profile> { new() };

        var publisher = CreatePublisher();
        var result = await publisher.PublishProfilesAsync(profiles, "test-source", WriteOrigin.Live);

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
        var result = await publisher.PublishFoodAsync(foods, "test-source", WriteOrigin.Live);

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
        _mockConnectorFoodEntryService
            .Setup(s => s.ImportAsync(OwnerSubjectId, It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectorFoodEntry> { new() });

        var publisher = CreatePublisher();
        var result = await publisher.PublishConnectorFoodEntriesAsync(entries, "test-source", WriteOrigin.Live);

        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        _mockConnectorFoodEntryService.Verify(
            s => s.ImportAsync(OwnerSubjectId, It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PublishActivityAsync_DelegatesToActivityService()
    {
        var activities = new List<Activity> { new() };

        var publisher = CreatePublisher();
        var result = await publisher.PublishActivityAsync(activities, "test-source", WriteOrigin.Live);

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

        var result = await publisher.PublishStateSpansAsync(spans, "test-source", WriteOrigin.Live);

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

        var result = await publisher.PublishStateSpansAsync(new List<StateSpan> { new() }, "test-source", WriteOrigin.Live);

        result.Should().BeFalse();
    }

}
