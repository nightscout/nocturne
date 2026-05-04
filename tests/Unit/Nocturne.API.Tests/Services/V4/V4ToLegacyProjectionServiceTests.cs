using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.API.Services.Treatments;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// Tests for the primary-record selection logic inside
/// <see cref="V4ToLegacyProjectionService.GetProjectedTreatmentsAsync"/> when a
/// single CorrelationId groups multiple boluses/carbs (N:M projection).
///
/// The service must pick the dominant-dose record as the primary "Meal Bolus",
/// with a deterministic Id tiebreaker so the output is stable across identical
/// requests regardless of the underlying storage-layer sort order.
/// </summary>
public class V4ToLegacyProjectionServiceTests
{
    private readonly Mock<ISensorGlucoseRepository> _sensorGlucoseRepo = new();
    private readonly Mock<IBolusRepository> _bolusRepo = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepo = new();
    private readonly Mock<IBGCheckRepository> _bgCheckRepo = new();
    private readonly Mock<INoteRepository> _noteRepo = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepo = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepo = new();
    private readonly Mock<IBolusCalculationRepository> _bolusCalcRepo = new();
    private readonly Mock<ITreatmentFoodService> _treatmentFoodService = new();
    private readonly V4ToLegacyProjectionService _service;

    public V4ToLegacyProjectionServiceTests()
    {
        // Empty defaults for the record types we don't exercise in these tests.
        _bgCheckRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<BGCheck>());

        _noteRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<Note>());

        _deviceEventRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<DeviceEvent>());

        _treatmentFoodService
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<TreatmentFood>());

        _tempBasalRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<TempBasal>());

        _bolusCalcRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<BolusCalculation>());

        var dbContext = TestDbContextFactory.CreateInMemoryContext();

        _service = new V4ToLegacyProjectionService(
            _sensorGlucoseRepo.Object,
            _bolusRepo.Object,
            _carbIntakeRepo.Object,
            _bgCheckRepo.Object,
            _noteRepo.Object,
            _deviceEventRepo.Object,
            _tempBasalRepo.Object,
            _bolusCalcRepo.Object,
            _treatmentFoodService.Object,
            dbContext,
            NullLogger<V4ToLegacyProjectionService>.Instance
        );
    }

    private void SetupBoluses(IEnumerable<Bolus> boluses) =>
        _bolusRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(boluses);

    private void SetupCarbs(IEnumerable<CarbIntake> carbs) =>
        _carbIntakeRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(carbs);

    [Fact]
    public async Task GetProjectedTreatments_MultipleBolusesInCorrelation_SelectsLargestAsMealBolus()
    {
        var correlationId = Guid.CreateVersion7();
        var timestamp = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);

        var smallBolus = new Bolus
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 2.0,
        };
        var largeBolus = new Bolus
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 5.0,
        };
        var carb = new CarbIntake
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 45.0,
        };

        // Intentionally present boluses in ascending-insulin order so a naive
        // "take the first" selector would pick the 2u bolus.
        SetupBoluses(new[] { smallBolus, largeBolus });
        SetupCarbs(new[] { carb });

        var result = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        var mealBolus = result.Single(t => t.EventType == TreatmentTypes.MealBolus);
        mealBolus.Insulin.Should().Be(5.0);
        mealBolus.Id.Should().Be(largeBolus.Id.ToString());

        var correction = result.Single(t => t.EventType == TreatmentTypes.CorrectionBolus);
        correction.Insulin.Should().Be(2.0);
        correction.Id.Should().Be(smallBolus.Id.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatments_MultipleCarbsInCorrelation_SelectsLargestAsMealBolus()
    {
        var correlationId = Guid.CreateVersion7();
        var timestamp = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);

        var bolus = new Bolus
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 4.0,
        };
        var smallCarb = new CarbIntake
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 15.0,
        };
        var largeCarb = new CarbIntake
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 60.0,
        };

        SetupBoluses(new[] { bolus });
        SetupCarbs(new[] { smallCarb, largeCarb });

        var result = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        var mealBolus = result.Single(t => t.EventType == TreatmentTypes.MealBolus);
        mealBolus.Carbs.Should().Be(60.0);

        var carbCorrection = result.Single(t => t.EventType == TreatmentTypes.CarbCorrection);
        carbCorrection.Carbs.Should().Be(15.0);
        carbCorrection.Id.Should().Be(smallCarb.Id.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatments_EqualInsulinInCorrelation_TiebreaksByIdAscending()
    {
        // Two boluses with equal Insulin, equal Timestamp, equal CorrelationId.
        // The tiebreaker must be Id ascending — stable across request order.
        var correlationId = Guid.CreateVersion7();
        var timestamp = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);

        // Deterministic, easily-ordered Ids.
        var lowId = new Guid("00000000-0000-0000-0000-000000000001");
        var highId = new Guid("00000000-0000-0000-0000-000000000002");

        var b1 = new Bolus
        {
            Id = lowId,
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 3.0,
        };
        var b2 = new Bolus
        {
            Id = highId,
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 3.0,
        };
        var carb = new CarbIntake
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 45.0,
        };

        // First invocation: b1 before b2.
        SetupBoluses(new[] { b1, b2 });
        SetupCarbs(new[] { carb });
        var result1 = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();
        var mealBolus1 = result1.Single(t => t.EventType == TreatmentTypes.MealBolus);

        // Second invocation: input order reversed. Output must be identical.
        SetupBoluses(new[] { b2, b1 });
        var result2 = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();
        var mealBolus2 = result2.Single(t => t.EventType == TreatmentTypes.MealBolus);

        mealBolus1.Id.Should().Be(mealBolus2.Id);
        // And specifically, the lower-Id record wins.
        mealBolus1.Id.Should().Be(lowId.ToString());
    }

    [Fact]
    public async Task GetProjectedTreatments_EqualCarbsInCorrelation_TiebreaksByIdAscending()
    {
        var correlationId = Guid.CreateVersion7();
        var timestamp = new DateTime(2025, 01, 01, 12, 0, 0, DateTimeKind.Utc);

        var lowId = new Guid("00000000-0000-0000-0000-000000000001");
        var highId = new Guid("00000000-0000-0000-0000-000000000002");

        var bolus = new Bolus
        {
            Id = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Insulin = 4.0,
        };
        var c1 = new CarbIntake
        {
            Id = lowId,
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 30.0,
        };
        var c2 = new CarbIntake
        {
            Id = highId,
            CorrelationId = correlationId,
            Timestamp = timestamp,
            Carbs = 30.0,
        };

        SetupBoluses(new[] { bolus });
        SetupCarbs(new[] { c1, c2 });
        var result1 = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        SetupCarbs(new[] { c2, c1 });
        var result2 = (await _service.GetProjectedTreatmentsAsync(null, null, 100)).ToList();

        // The Meal Bolus projection carries the primary bolus Id, not the carb
        // Id, so the pairing's stability is verified through the leftover
        // CarbCorrection: the higher-Id carb must always be the leftover.
        var leftover1 = result1.Single(t => t.EventType == TreatmentTypes.CarbCorrection);
        var leftover2 = result2.Single(t => t.EventType == TreatmentTypes.CarbCorrection);
        leftover1.Id.Should().Be(leftover2.Id).And.Be(highId.ToString());
    }
}
