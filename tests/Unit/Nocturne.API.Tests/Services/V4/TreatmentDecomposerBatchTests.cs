using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

public class TreatmentDecomposerBatchTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly Mock<IBolusRepository> _bolusRepoMock;
    private readonly Mock<ICarbIntakeRepository> _carbRepoMock;
    private readonly Mock<IBGCheckRepository> _bgCheckRepoMock;
    private readonly Mock<INoteRepository> _noteRepoMock;
    private readonly Mock<IBolusCalculationRepository> _bolusCalcRepoMock;
    private readonly Mock<IDeviceEventRepository> _deviceEventRepoMock;
    private readonly Mock<ITempBasalRepository> _tempBasalRepoMock;
    private readonly Mock<IStateSpanService> _stateSpanServiceMock;
    private readonly Mock<ITreatmentFoodService> _treatmentFoodServiceMock;
    private readonly Mock<IDeviceService> _deviceServiceMock;
    private readonly Mock<IProfileDecomposer> _profileDecomposerMock;
    private readonly Mock<IActiveProfileResolver> _activeProfileResolverMock;
    private readonly Mock<IPatientInsulinRepository> _insulinRepoMock;
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerBatchTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        _bolusRepoMock = new Mock<IBolusRepository>();
        _carbRepoMock = new Mock<ICarbIntakeRepository>();
        _bgCheckRepoMock = new Mock<IBGCheckRepository>();
        _noteRepoMock = new Mock<INoteRepository>();
        _bolusCalcRepoMock = new Mock<IBolusCalculationRepository>();
        _deviceEventRepoMock = new Mock<IDeviceEventRepository>();
        _tempBasalRepoMock = new Mock<ITempBasalRepository>();
        _stateSpanServiceMock = new Mock<IStateSpanService>();
        _treatmentFoodServiceMock = new Mock<ITreatmentFoodService>();
        _deviceServiceMock = new Mock<IDeviceService>();
        _profileDecomposerMock = new Mock<IProfileDecomposer>();
        _activeProfileResolverMock = new Mock<IActiveProfileResolver>();
        _insulinRepoMock = new Mock<IPatientInsulinRepository>();

        NothingHeld<IBolusRepository, V4Models.Bolus>(_bolusRepoMock);
        NothingHeld<ICarbIntakeRepository, V4Models.CarbIntake>(_carbRepoMock);
        NothingHeld<IBGCheckRepository, V4Models.BGCheck>(_bgCheckRepoMock);
        NothingHeld<INoteRepository, V4Models.Note>(_noteRepoMock);
        NothingHeld<IBolusCalculationRepository, V4Models.BolusCalculation>(_bolusCalcRepoMock);
        NothingHeld<IDeviceEventRepository, V4Models.DeviceEvent>(_deviceEventRepoMock);
        NothingHeld<ITempBasalRepository, V4Models.TempBasal>(_tempBasalRepoMock);

        // BulkUpsertAsync returns the input records
        _bolusRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.Bolus> records, WriteOrigin origin, CancellationToken _) => [.. records]);
        _carbRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.CarbIntake>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.CarbIntake> records, WriteOrigin origin, CancellationToken _) => [.. records]);
        _bgCheckRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.BGCheck>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.BGCheck> records, WriteOrigin origin, CancellationToken _) => [.. records]);
        _noteRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.Note>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.Note> records, WriteOrigin origin, CancellationToken _) => [.. records]);
        _bolusCalcRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.BolusCalculation>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.BolusCalculation> records, WriteOrigin origin, CancellationToken _) => [.. records]);
        _deviceEventRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.DeviceEvent>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.DeviceEvent> records, WriteOrigin origin, CancellationToken _) => [.. records]);
        _tempBasalRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.TempBasal>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.TempBasal> records, WriteOrigin origin, CancellationToken _) => [.. records]);

        // StateSpanService returns a new StateSpan
        _stateSpanServiceMock
            .Setup(x => x.UpsertStateSpanWithOutcomeAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan span, CancellationToken _) => new StateSpanUpsert(span, StateSpanUpsertOutcome.Inserted));

        // UpdateAsync returns the input bolus (for linking pass)
        _bolusRepoMock
            .Setup(x => x.UpdateAsync(It.IsAny<Guid>(), It.IsAny<V4Models.Bolus>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, V4Models.Bolus b, WriteOrigin origin, CancellationToken _) => b);

        // DeviceService returns null by default
        _deviceServiceMock
            .Setup(s => s.ResolveAsync(It.IsAny<V4Models.DeviceCategory>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        _decomposer = new TreatmentDecomposer(
            _context,
            _bolusRepoMock.Object,
            _tempBasalRepoMock.Object,
            _carbRepoMock.Object,
            _bgCheckRepoMock.Object,
            _noteRepoMock.Object,
            _deviceEventRepoMock.Object,
            _bolusCalcRepoMock.Object,
            _stateSpanServiceMock.Object,
            _treatmentFoodServiceMock.Object,
            _deviceServiceMock.Object,
            Mock.Of<IPatientDeviceStamper>(),
            _profileDecomposerMock.Object,
            _activeProfileResolverMock.Object,
            _insulinRepoMock.Object,
            Mock.Of<IAuditContext>(),
            Mock.Of<IDeduplicationService>(),
            NullLogger<TreatmentDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DecomposeBatchAsync_ClassifiesAndBulkInserts()
    {
        // Arrange — batch with a bolus, carb correction, note, and bg check
        var treatments = new List<Treatment>
        {
            new() { Id = "bolus-1", EventType = "Correction Bolus", Mills = 1700000000000, Insulin = 2.5 },
            new() { Id = "carb-1", EventType = "Carb Correction", Mills = 1700000001000, Carbs = 15 },
            new() { Id = "note-1", EventType = "Note", Mills = 1700000002000, Notes = "Felt low" },
            new() { Id = "bg-1", EventType = "BG Check", Mills = 1700000003000, Glucose = 95, GlucoseType = "Finger" },
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        // Assert — correct partition sizes
        _bolusRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.Bolus>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _carbRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.CarbIntake>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _noteRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.Note>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _bgCheckRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.BGCheck>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // No bolus calc, device event, or temp basal calls
        _bolusCalcRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.BolusCalculation>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _deviceEventRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.DeviceEvent>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _tempBasalRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.TempBasal>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().HaveCount(4);
        result.CorrelationId.Should().NotBeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_EmptyBatch_NoRepositoryCalls()
    {
        // Act
        var result = await _decomposer.DecomposeBatchAsync([], WriteOrigin.Live);

        // Assert
        _bolusRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _carbRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.CarbIntake>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _bgCheckRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.BGCheck>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _noteRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.Note>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _bolusCalcRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.BolusCalculation>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _deviceEventRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.DeviceEvent>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _tempBasalRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.TempBasal>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().BeEmpty();
        result.CorrelationId.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_StateSpanTreatments_UsesTempBasalBulkAndIndividualUpsert()
    {
        // Arrange — temp basal (bulk-insertable) + temporary target (individual upsert)
        var treatments = new List<Treatment>
        {
            new() { Id = "tb-1", EventType = "Temp Basal", Mills = 1700000000000, Duration = 30, Absolute = 1.5 },
            new() { Id = "tt-1", EventType = "Temporary Target", Mills = 1700000001000, Duration = 60, TargetTop = 120, TargetBottom = 100 },
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        // Assert — temp basal uses bulk insert
        _tempBasalRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.TempBasal>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Temporary target uses individual state span upsert
        _stateSpanServiceMock.Verify(
            x => x.UpsertStateSpanWithOutcomeAsync(
                It.Is<StateSpan>(s => s.Category == StateSpanCategory.TemporaryTarget),
                It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(2);
    }

    [Fact]
    public async Task DecomposeBatchAsync_LinksBolusToCalculation()
    {
        // Arrange — Bolus Wizard treatment that produces both bolus and calculation
        var treatments = new List<Treatment>
        {
            new()
            {
                Id = "bw-1",
                EventType = "Bolus Wizard",
                Mills = 1700000000000,
                Insulin = 3.0,
                Carbs = 30,
                BloodGlucoseInput = 150,
                CR = 10,
            },
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        // Assert — both bolus and calculation created
        _bolusRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.Bolus>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _bolusCalcRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.BolusCalculation>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Carb intake also produced (insulin + carbs override rule)
        _carbRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.CarbIntake>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Linking pass: UpdateAsync called to set BolusCalculationId on the bolus
        _bolusRepoMock.Verify(
            x => x.UpdateAsync(It.IsAny<Guid>(), It.IsAny<V4Models.Bolus>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Verify the bolus in CreatedRecords has a linked BolusCalculationId
        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().Single();
        var calc = result.CreatedRecords.OfType<V4Models.BolusCalculation>().Single();
        bolus.BolusCalculationId.Should().Be(calc.Id);
    }

    [Fact]
    public async Task DecomposeBatchAsync_GivesEachTreatmentItsOwnCorrelationId()
    {
        var treatments = new List<Treatment>
        {
            new() { Id = "meal-1", EventType = "Meal Bolus", Mills = 1700000000000, Insulin = 5.0, Carbs = 45 },
            new() { Id = "meal-2", EventType = "Meal Bolus", Mills = 1700003600000, Insulin = 3.0, Carbs = 30 },
            new() { Id = "carbs-3", EventType = "Carb Correction", Mills = 1700007200000, Carbs = 15 },
        };

        var result = await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        var records = result.CreatedRecords.OfType<V4Models.IV4Record>().ToList();
        records.Should().HaveCount(5);
        records.Should().OnlyContain(r => r.CorrelationId.HasValue && r.CorrelationId != Guid.Empty);

        var idsByTreatment = records
            .GroupBy(r => r.LegacyId)
            .ToDictionary(g => g.Key!, g => g.Select(r => r.CorrelationId!.Value).Distinct().ToList());
        idsByTreatment.Should().HaveCount(3);
        idsByTreatment.Values.Should().OnlyContain(ids => ids.Count == 1, "a meal bolus's bolus and carb intake are siblings");
        idsByTreatment.Values.Select(ids => ids[0]).Should().OnlyHaveUniqueItems("treatments are separate source records");

        result.CorrelationId.Should().Be(idsByTreatment["meal-1"][0]);
    }

    [Fact]
    public async Task DecomposeBatchAsync_MealBolus_ProducesBothBolusAndCarbIntake()
    {
        // Arrange — Meal Bolus should produce both bolus + carb
        var treatments = new List<Treatment>
        {
            new() { Id = "meal-1", EventType = "Meal Bolus", Mills = 1700000000000, Insulin = 5.0, Carbs = 45 },
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        // Assert
        _bolusRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.Bolus>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _carbRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.CarbIntake>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(2);
    }

    /// <summary>
    /// A bulk-uploaded meal treatment's <c>foodType</c> is preserved as its carb intake's
    /// <see cref="TreatmentFood"/> line, as on the single-record path — the carb intake's id is
    /// only known once the bulk write has persisted it, so the line is written afterwards.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_MealWithFoodType_WritesTreatmentFoodLine()
    {
        // Arrange — the persisted carb intakes carry repository-assigned ids
        var mealCarbIntakeId = Guid.CreateVersion7();
        _carbRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.CarbIntake>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.CarbIntake> records, WriteOrigin _, CancellationToken _) =>
            {
                var persisted = records.ToList();
                foreach (var record in persisted)
                    record.Id = record.LegacyId == "meal-1" ? mealCarbIntakeId : Guid.CreateVersion7();
                return [.. persisted];
            });

        var treatments = new List<Treatment>
        {
            new() { Id = "meal-1", EventType = "Meal Bolus", Mills = 1700000000000, Insulin = 5.0, Carbs = 45, FoodType = "Sandwich" },
            new() { Id = "carb-1", EventType = "Carb Correction", Mills = 1700000001000, Carbs = 15 },
        };

        // Act
        await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        // Assert — one line, on the meal's carb intake, carrying the legacy food type
        _treatmentFoodServiceMock.Verify(
            x => x.AddAsync(
                It.Is<TreatmentFood>(f =>
                    f.CarbIntakeId == mealCarbIntakeId && f.Note == "Sandwich" && f.Carbs == 45m),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // The carb correction names no food, so it gets no line
        _treatmentFoodServiceMock.Verify(
            x => x.AddAsync(It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A connector replaying its catch-up overlap window re-sends a carb intake the bulk write
    /// upserts in place on its sync key, which it reports as updated, so the food line must not be
    /// written a second time. Those rows are the user-editable food breakdown and feed the legacy
    /// projection, so a duplicate per replay compounds.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_SyncUpsertedCarbIntakeAlreadyHasFoodLine_WritesNoSecondLine()
    {
        // Arrange — the bulk write returns the stored row it upserted in place
        var storedCarbIntakeId = Guid.CreateVersion7();
        _carbRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.CarbIntake>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.CarbIntake> records, WriteOrigin _, CancellationToken _) =>
            {
                var upserted = records.ToList();
                foreach (var record in upserted)
                    record.Id = storedCarbIntakeId;
                return new BulkWrite<V4Models.CarbIntake>(upserted, 0) { Updated = upserted };
            });

        var treatments = new List<Treatment>
        {
            new()
            {
                Id = "meal-1",
                EventType = "Meal Bolus",
                Mills = 1700000000000,
                Insulin = 5.0,
                Carbs = 45,
                FoodType = "Sandwich",
                SyncIdentifier = "sync-1",
                DataSource = "dexcom-connector",
            },
        };

        // Act
        await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        // Assert
        _treatmentFoodServiceMock.Verify(
            x => x.AddAsync(It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The bulk write keeps the last record of a legacy id repeated in the batch, so the food line
    /// must describe that same last treatment rather than an earlier duplicate's food type.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_DuplicateLegacyId_FoodLineFollowsTheInsertedTreatment()
    {
        // Arrange — emulate the bulk write's keep-last dedup by legacy id
        var carbIntakeId = Guid.CreateVersion7();
        _carbRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.CarbIntake>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.CarbIntake> records, WriteOrigin _, CancellationToken _) =>
            {
                var inserted = records.GroupBy(r => r.LegacyId!).Select(g => g.Last()).ToList();
                foreach (var record in inserted)
                    record.Id = carbIntakeId;
                return [.. inserted];
            });

        var treatments = new List<Treatment>
        {
            new() { Id = "dupe-1", EventType = "Carb Correction", Mills = 1700000000000, Carbs = 45, FoodType = "Sandwich" },
            new() { Id = "dupe-1", EventType = "Carb Correction", Mills = 1700000000000, Carbs = 45, FoodType = "Pizza" },
        };

        // Act
        await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        // Assert
        _treatmentFoodServiceMock.Verify(
            x => x.AddAsync(
                It.Is<TreatmentFood>(f => f.CarbIntakeId == carbIntakeId && f.Note == "Pizza"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _treatmentFoodServiceMock.Verify(
            x => x.AddAsync(It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DecomposeBatchAsync_ProfileSwitch_DelegatesToStateSpanService()
    {
        // Arrange
        var treatments = new List<Treatment>
        {
            new() { Id = "ps-1", EventType = "Profile Switch", Mills = 1700000000000, Profile = "Default", Duration = 0 },
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        // Assert — profile switch uses individual upsert, not bulk insert
        _stateSpanServiceMock.Verify(
            x => x.UpsertStateSpanWithOutcomeAsync(
                It.Is<StateSpan>(s => s.Category == StateSpanCategory.Profile),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _tempBasalRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.TempBasal>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().HaveCount(1);
    }

    /// <summary>
    /// A v4-native treatment is written back under its record's uuid prefix; the pull-back must
    /// land on that record. The decomposer offers every treatment table the ids that can name a
    /// stored record, before the upserts, and gives the adopting record's correlation siblings the
    /// same id in every table (the carbs of a meal whose bolus took it).
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_AdoptsTheWireIdOnTheStoredTreatmentBeforeUpserting()
    {
        const string wireId = "0198c2a41f3b7c2d9e556a1b";
        var correlationId = Guid.CreateVersion7();
        var calls = new List<string>();
        _bolusRepoMock
            .Setup(x => x.FindUnkeyedOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UnkeyedOwnId(wireId, WriteBackMaySend: false)]);
        _bolusRepoMock
            .Setup(x => x.AdoptOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<string> ids, CancellationToken _) => calls.Add($"bolus.adopt({string.Join(",", ids)})"))
            .ReturnsAsync([new V4Models.Bolus { LegacyId = wireId, CorrelationId = correlationId }]);
        _carbRepoMock
            .Setup(x => x.AdoptLegacyIdsByCorrelationAsync(It.IsAny<IReadOnlyDictionary<Guid, string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyDictionary<Guid, string> map, CancellationToken _) => calls.Add($"carb.siblings({map[correlationId]})"))
            .ReturnsAsync(1);
        _bolusRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("bolus.upsert"))
            .ReturnsAsync((IEnumerable<V4Models.Bolus> records, WriteOrigin _, CancellationToken _) => [.. records]);

        await _decomposer.DecomposeBatchAsync(
        [
            new Treatment { Id = wireId, EventType = "Meal Bolus", Mills = 1700000000000, Insulin = 2.5, Carbs = 40 },
            new Treatment { Id = "aaps-bolus-7", EventType = "Correction Bolus", Mills = 1700000300000, Insulin = 1 },
        ], WriteOrigin.Live);

        calls.Should().Equal($"bolus.adopt({wireId})", $"carb.siblings({wireId})", "bolus.upsert");
        VerifyOffered<ICarbIntakeRepository, V4Models.CarbIntake>(_carbRepoMock, wireId);
        VerifyOffered<IBGCheckRepository, V4Models.BGCheck>(_bgCheckRepoMock, wireId);
        VerifyOffered<INoteRepository, V4Models.Note>(_noteRepoMock, wireId);
        VerifyOffered<IBolusCalculationRepository, V4Models.BolusCalculation>(_bolusCalcRepoMock, wireId);
        VerifyOffered<IDeviceEventRepository, V4Models.DeviceEvent>(_deviceEventRepoMock, wireId);
        VerifyOffered<ITempBasalRepository, V4Models.TempBasal>(_tempBasalRepoMock, wireId);
    }

    private static void VerifyOffered<TRepo, TRecord>(Mock<TRepo> repo, string wireId)
        where TRepo : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, V4Models.IV4Record
        => repo.Verify(
            x => x.AdoptOwnIdsAsync(It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { wireId })), It.IsAny<CancellationToken>()),
            Times.Once);

    [Fact]
    public async Task DecomposeAsync_AdoptsTheCanonicalUuidOfAStoredTreatment_AndSkipsIdsThatNameNone()
    {
        const string uuid = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";
        _noteRepoMock
            .Setup(x => x.FindUnkeyedOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, string _, CancellationToken _) =>
                ids.Where(i => i == uuid).Select(i => new UnkeyedOwnId(i, WriteBackMaySend: false)));
        _noteRepoMock
            .Setup(x => x.AdoptOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, CancellationToken _) =>
                ids.Where(i => i == uuid).Select(i => new V4Models.Note { LegacyId = i }));

        await _decomposer.DecomposeAsync(
            new Treatment { Id = uuid, EventType = "Note", Mills = 1700000000000, Notes = "echo" }, WriteOrigin.Live);
        await _decomposer.DecomposeAsync(
            new Treatment { Id = "aaps-note-1", EventType = "Note", Mills = 1700000300000, Notes = "upload" }, WriteOrigin.Live);

        _noteRepoMock.Verify(
            x => x.AdoptOwnIdsAsync(It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { uuid })), It.IsAny<CancellationToken>()),
            Times.Once);
        _noteRepoMock.Verify(
            x => x.AdoptOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A treatment the Nightscout connector pulls under a <c>_id</c> Nightscout minted, whose
    /// identifier names a bolus write-back may have sent upstream, is that bolus's write-back echo: it takes
    /// the bolus's id and writes nothing, so the bolus keeps its attribution and any edit made since.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_WritesNothingForAWriteBackEcho()
    {
        const string legacyId = "loop-sync-3a7c";
        NothingHeld<ICarbIntakeRepository, V4Models.CarbIntake>(_carbRepoMock);
        NothingHeld<IBGCheckRepository, V4Models.BGCheck>(_bgCheckRepoMock);
        NothingHeld<INoteRepository, V4Models.Note>(_noteRepoMock);
        NothingHeld<IBolusCalculationRepository, V4Models.BolusCalculation>(_bolusCalcRepoMock);
        NothingHeld<IDeviceEventRepository, V4Models.DeviceEvent>(_deviceEventRepoMock);
        NothingHeld<ITempBasalRepository, V4Models.TempBasal>(_tempBasalRepoMock);
        _bolusRepoMock
            .Setup(x => x.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { legacyId });
        _bolusRepoMock
            .Setup(x => x.GetLegacyIdsWriteBackMaySendAsync(
                It.IsAny<IReadOnlyCollection<string>>(), "nightscout-connector", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, string _, CancellationToken _) => ids.Where(i => i == legacyId));
        List<V4Models.Bolus>? written = null;
        _bolusRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<V4Models.Bolus> records, WriteOrigin _, CancellationToken _) => written = [.. records])
            .ReturnsAsync((IEnumerable<V4Models.Bolus> records, WriteOrigin _, CancellationToken _) => [.. records]);
        var echo = new Treatment
        {
            Id = "66f0a1b2c3d4e0f6a7b8c9d0", UpstreamIdentifier = legacyId, EventType = "Correction Bolus",
            Mills = 1700000000000, Insulin = 2.5, DataSource = "nightscout-connector",
        };
        var pulled = new Treatment
        {
            Id = "66f0a1b2c3d4e0f6a7b8c9d1", EventType = "Correction Bolus",
            Mills = 1700000300000, Insulin = 1, DataSource = "nightscout-connector",
        };

        var echoes = await _decomposer.ResolveStoredIdentitiesAsync([echo, pulled]);
        await _decomposer.DecomposeBatchAsync([echo, pulled], WriteOrigin.Live);

        echoes.Should().BeEquivalentTo([echo]);
        echo.Id.Should().Be(legacyId);
        written!.Select(b => b.LegacyId).Should().Equal("66f0a1b2c3d4e0f6a7b8c9d1");
    }

    private static Treatment PulledBolus(string id, string? identifier, double insulin = 2.5) => new()
    {
        Id = id, UpstreamIdentifier = identifier, EventType = "Correction Bolus",
        Mills = 1700000000000, Insulin = insulin, DataSource = "nightscout-connector",
    };

    /// <summary>
    /// Treatment write-back sends a legacy id that is neither an ObjectId nor a uuid as its hash, in
    /// <c>_id</c> and <c>identifier</c> alike, and 15.0.7 and later keep only the identifier. The copy
    /// is pointed at the treatment the hash names; a treatment only an import wrote is the
    /// upstream's own record, so the pull updates it.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_UpdatesTheTreatmentAPulledHashNames_WhenWriteBackNeverSentIt()
    {
        const string legacyId = "syn-3a7c0e9f1b2d4c6e";
        var hash = MongoObjectId.Coerce(legacyId)!;
        _bolusRepoMock
            .Setup(x => x.ResolveHashedLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, CancellationToken _) =>
                ids.Where(i => i == hash).Select(i => new WireLegacyId(i, legacyId)));
        List<V4Models.Bolus>? written = null;
        _bolusRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<V4Models.Bolus> records, WriteOrigin _, CancellationToken _) => written = [.. records])
            .ReturnsAsync((IEnumerable<V4Models.Bolus> records, WriteOrigin _, CancellationToken _) => [.. records]);
        var copy = PulledBolus("66f0a1b2c3d4e0f6a7b8c9d0", hash, insulin: 0);

        await _decomposer.DecomposeBatchAsync([copy], WriteOrigin.Live);

        copy.Id.Should().Be(legacyId);
        written!.Select(b => (b.LegacyId, b.Insulin)).Should().Equal((legacyId, 0d));
    }

    [Fact]
    public async Task DecomposeBatchAsync_WritesNothingForTheEchoOfATreatmentAPulledHashNames()
    {
        const string legacyId = "syn-3a7c0e9f1b2d4c6e";
        var hash = MongoObjectId.Coerce(legacyId)!;
        _bolusRepoMock
            .Setup(x => x.ResolveHashedLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, CancellationToken _) =>
                ids.Where(i => i == hash).Select(i => new WireLegacyId(i, legacyId)));
        _bolusRepoMock
            .Setup(x => x.GetLegacyIdsWriteBackMaySendAsync(
                It.IsAny<IReadOnlyCollection<string>>(), "nightscout-connector", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, string _, CancellationToken _) => ids.Where(i => i == legacyId));
        var bolusWrites = 0;
        _bolusRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(() => bolusWrites++)
            .ReturnsAsync((IEnumerable<V4Models.Bolus> records, WriteOrigin _, CancellationToken _) => [.. records]);

        await _decomposer.DecomposeBatchAsync([PulledBolus(hash, hash)], WriteOrigin.Live);

        bolusWrites.Should().Be(0);
    }

    /// <summary>
    /// An earlier write-back sent an edit under the id the treatment was served by, whatever its
    /// legacy id: its own uuid up to v0.2.3, that uuid's prefix from v0.2.4. That copy is pointed at
    /// the treatment too.
    /// </summary>
    [Theory]
    [InlineData("0198c2a41f3b7c2d9e556a1b")]
    [InlineData("0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f")]
    public async Task ResolveStoredIdentitiesAsync_PointsAPulledCopyNamingATreatmentByItsServedIdAtItsLegacyId(string servedId)
    {
        _carbRepoMock
            .Setup(x => x.ResolveKeyedOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, CancellationToken _) =>
                ids.Where(i => i == servedId).Select(i => new WireLegacyId(i, "65a1b2c3d4e5f60718293a4b")));
        var copy = PulledBolus("66f0a1b2c3d4e0f6a7b8c9d0", servedId);

        await _decomposer.ResolveStoredIdentitiesAsync([copy]);

        copy.Id.Should().Be("65a1b2c3d4e5f60718293a4b");
    }

    /// <summary>
    /// A connector sync selects the treatments to publish, resolves them for their fingerprints, and
    /// decomposes them. Each treatment's identity is looked up once across the three, and the
    /// selection, a read, gives no record an id: that waits for the publish.
    /// </summary>
    [Fact]
    public async Task ASync_LooksEachIdentityUpOnce_AndGivesARecordItsIdOnlyWhenPublishing()
    {
        const string wireId = "0198c2a41f3b7c2d9e556a1b";
        var adopted = 0;
        _bolusRepoMock
            .Setup(x => x.FindUnkeyedOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UnkeyedOwnId(wireId, WriteBackMaySend: false)]);
        _bolusRepoMock
            .Setup(x => x.AdoptOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => adopted++)
            .ReturnsAsync([new V4Models.Bolus { LegacyId = wireId }]);
        var copy = PulledBolus("66f0a1b2c3d4e0f6a7b8c9d0", wireId);

        var selected = await _decomposer.SelectForRepublishAsync("nightscout-connector", [copy]);
        adopted.Should().Be(0);
        await _decomposer.ResolveStoredIdentitiesAsync(selected);
        adopted.Should().Be(1);
        await _decomposer.DecomposeBatchAsync([copy], WriteOrigin.Live);

        selected.Should().Equal(copy);
        copy.Id.Should().Be(wireId);
        adopted.Should().Be(1);
        _bolusRepoMock.Verify(
            x => x.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _bolusRepoMock.Verify(
            x => x.FindUnkeyedOwnIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static void NothingHeld<TRepo, TRecord>(Mock<TRepo> repo)
        where TRepo : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, V4Models.IV4Record
    {
        repo.Setup(x => x.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        repo.ForwardCreateOrUpsertToCreate<TRepo, TRecord>();
    }
}

