using FluentAssertions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// A food entry is attributed to the carb intake the store holds under its legacy id. The store
/// skips a carb intake it already holds, so the record a sync publishes names a stored row only on
/// the sync that inserted it.
/// </summary>
public class GlookoConnectorServiceFoodAttributionTests
{
    private static readonly SyncDataType[] OtherTypes = [SyncDataType.StateSpans, SyncDataType.TempBasals];

    [Fact]
    public async Task SyncDataAsync_WhenTheSameMealSyncsTwice_AttributesItsFoodsToTheCarbIntakeTheFirstSyncStored()
    {
        var harness = new Harness();
        var service = harness.Service();

        await harness.SyncAsync(service, carbIntakes: true);
        await harness.SyncAsync(service, carbIntakes: true);

        harness.Store.Offered[nameof(CarbIntake)].Should().Be(2, "each sync hands the store its own copy");
        harness.StoredCarbIntake.Id.Should().NotBeEmpty();
        harness.MealMatching.Verify(
            m => m.AcceptMatchAsync(
                harness.FoodEntryId, harness.StoredCarbIntake.Id, 12m, 0, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        harness.MealMatching.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SyncDataAsync_WhenCarbIntakesAreOffAndNoneIsStored_AttributesNothing()
    {
        var harness = new Harness();

        var result = await harness.SyncAsync(harness.Service(), carbIntakes: false);

        result.Success.Should().BeTrue();
        harness.Store.Stored.Keys.Should().NotContain(key => key.Type == nameof(CarbIntake));
        harness.MealMatching.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SyncDataAsync_WhenCarbIntakesAreOffButAnEarlierSyncStoredThem_AttributesToTheStoredCarbIntake()
    {
        var harness = new Harness();
        var service = harness.Service();
        await harness.SyncAsync(service, carbIntakes: true);

        await harness.SyncAsync(service, carbIntakes: false);

        harness.Store.Offered[nameof(CarbIntake)].Should().Be(1, "the second sync publishes no carb intake");
        harness.MealMatching.Verify(
            m => m.AcceptMatchAsync(
                harness.FoodEntryId, harness.StoredCarbIntake.Id, 12m, 0, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        harness.MealMatching.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SyncDataAsync_AttributesAFoodToItsStoredCarbIntakeWhetherOrNotGlookoGaveItAGuid(
        bool useV3Api, bool guidless)
    {
        var harness = new Harness(useV3Api, guidless);
        var service = harness.Service();

        await harness.SyncAsync(service, carbIntakes: true);

        harness.Store.Stored.Keys.Count(key => key.Type == nameof(CarbIntake)).Should().Be(1);
        harness.MealMatching.Verify(
            m => m.AcceptMatchAsync(
                harness.FoodEntryId, harness.StoredCarbIntake.Id, 12m, 0, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.MealMatching.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true, "Attributed 1/1 food entries")]
    [InlineData(false, "Attributed 0/1 food entries")]
    public async Task SyncDataAsync_CountsAFoodEntryAsAttributedOnlyWhenTheMatchWasLinked(
        bool linked, string expectedLine)
    {
        var harness = new Harness();
        harness.AcceptMatchReports(linked);

        await harness.SyncAsync(harness.Service(), carbIntakes: true);

        harness.Log.Messages.Should().ContainSingle(m => m.Contains("Attributed "))
            .Which.Should().Contain(expectedLine);
    }

    private sealed class Harness
    {
        private readonly DateTime _from = DateTime.UtcNow.AddDays(-3);
        private readonly bool _useV3Api;
        private readonly bool _guidless;

        public Harness(bool useV3Api = false, bool guidless = false)
        {
            _useV3Api = useV3Api;
            _guidless = guidless;
            Metadata
                .Setup(m => m.PublishConnectorFoodEntriesAsync(
                    It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(), It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<ConnectorFoodEntryImport> imports, string _, WriteOrigin _, CancellationToken _) =>
                {
                    var externalEntryId = imports.Single().ExternalEntryId.Trim();
                    return
                    [
                        new ConnectorFoodEntry
                        {
                            Id = FoodEntryId,
                            ExternalEntryId = externalEntryId,
                            Carbs = 12m,
                            Status = ConnectorFoodEntryStatus.Pending,
                        },
                    ];
                });

            var publisher = new Mock<IConnectorPublisher>();
            publisher.SetupGet(p => p.IsAvailable).Returns(true);
            publisher.SetupGet(p => p.Treatments).Returns(Store);
            publisher.SetupGet(p => p.Metadata).Returns(Metadata.Object);
            Publisher = publisher.Object;
        }

        public Guid FoodEntryId { get; } = Guid.CreateVersion7();

        public StoringConnectorPublisher Store { get; } = new();

        public Mock<IMetadataPublisher> Metadata { get; } = new();

        public Mock<IMealMatchingService> MealMatching { get; } = new();

        public MessageLogger<GlookoConnectorService> Log { get; } = new();

        public void AcceptMatchReports(bool linked) =>
            MealMatching
                .Setup(m => m.AcceptMatchAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(linked);

        private IConnectorPublisher Publisher { get; }

        public CarbIntake StoredCarbIntake =>
            (CarbIntake)Store.Stored.Single(stored => stored.Key.Type == nameof(CarbIntake)).Value;

        public RecordingGlookoConnectorService Service() =>
            GlookoSyncHarness.Service(
                new GlookoEndpointHandler(withHistoryMeals: _useV3Api, withGuidlessFoods: _guidless), publisher: Publisher, mealMatchingService: MealMatching.Object, logger: Log);

        public Task<SyncResult> SyncAsync(RecordingGlookoConnectorService service, bool carbIntakes)
        {
            var request = new SyncRequest
            {
                DataTypes = [.. OtherTypes, SyncDataType.Food, .. carbIntakes ? [SyncDataType.CarbIntake] : Array.Empty<SyncDataType>()],
                From = _from,
            };
            return service.SyncDataAsync(request, GlookoSyncHarness.Config(_useV3Api), CancellationToken.None);
        }
    }
}
