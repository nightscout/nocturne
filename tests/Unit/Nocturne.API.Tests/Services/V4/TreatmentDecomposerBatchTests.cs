using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
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

        // BulkCreateAsync returns the input records
        _bolusRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.Bolus> records, WriteOrigin origin, CancellationToken _) => records);
        _carbRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.CarbIntake>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.CarbIntake> records, WriteOrigin origin, CancellationToken _) => records);
        _bgCheckRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.BGCheck>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.BGCheck> records, WriteOrigin origin, CancellationToken _) => records);
        _noteRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.Note>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.Note> records, WriteOrigin origin, CancellationToken _) => records);
        _bolusCalcRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.BolusCalculation>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.BolusCalculation> records, WriteOrigin origin, CancellationToken _) => records);
        _deviceEventRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.DeviceEvent>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.DeviceEvent> records, WriteOrigin origin, CancellationToken _) => records);
        _tempBasalRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.TempBasal>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.TempBasal> records, WriteOrigin origin, CancellationToken _) => records);

        // StateSpanService returns a new StateSpan
        _stateSpanServiceMock
            .Setup(x => x.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan span, CancellationToken _) => span);

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
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.Bolus>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _carbRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.CarbIntake>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _noteRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.Note>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _bgCheckRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.BGCheck>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // No bolus calc, device event, or temp basal calls
        _bolusCalcRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.BolusCalculation>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _deviceEventRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.DeviceEvent>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _tempBasalRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.TempBasal>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
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
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _carbRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.CarbIntake>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _bgCheckRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.BGCheck>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _noteRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.Note>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _bolusCalcRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.BolusCalculation>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _deviceEventRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.DeviceEvent>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _tempBasalRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.TempBasal>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
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
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.TempBasal>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Temporary target uses individual state span upsert
        _stateSpanServiceMock.Verify(
            x => x.UpsertStateSpanAsync(
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
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.Bolus>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _bolusCalcRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.BolusCalculation>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Carb intake also produced (insulin + carbs override rule)
        _carbRepoMock.Verify(
            x => x.BulkCreateAsync(
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
    public async Task DecomposeBatchAsync_ProducedRecordsShareCorrelationId()
    {
        // Arrange — one treatment that produces multiple sibling records (bolus + carb)
        var treatments = new List<Treatment>
        {
            new() { Id = "meal-1", EventType = "Meal Bolus", Mills = 1700000000000, Insulin = 5.0, Carbs = 45 },
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        // Assert — all sibling records share a single non-empty correlation id
        result.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
        result.CreatedRecords.OfType<V4Models.IV4Record>()
            .Should().NotBeEmpty()
            .And.OnlyContain(r => r.CorrelationId == result.CorrelationId);
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
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.Bolus>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _carbRepoMock.Verify(
            x => x.BulkCreateAsync(
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
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.CarbIntake>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.CarbIntake> records, WriteOrigin _, CancellationToken _) =>
            {
                var persisted = records.ToList();
                foreach (var record in persisted)
                    record.Id = record.LegacyId == "meal-1" ? mealCarbIntakeId : Guid.CreateVersion7();
                return persisted;
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
    /// upserts in place on its sync key — which still comes back in the created set — so the food
    /// line must not be written a second time. Those rows are the user-editable food breakdown and
    /// feed the legacy projection, so a duplicate per replay compounds.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_SyncUpsertedCarbIntakeAlreadyHasFoodLine_WritesNoSecondLine()
    {
        // Arrange — the bulk write returns the stored row it upserted in place, which already
        // carries the line written on first ingest
        var storedCarbIntakeId = Guid.CreateVersion7();
        _carbRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.CarbIntake>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.CarbIntake> records, WriteOrigin _, CancellationToken _) =>
            {
                var upserted = records.ToList();
                foreach (var record in upserted)
                    record.Id = storedCarbIntakeId;
                return upserted;
            });

        _treatmentFoodServiceMock
            .Setup(x => x.GetByCarbIntakeIdsAsync(
                It.Is<IEnumerable<Guid>>(ids => ids.Contains(storedCarbIntakeId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TreatmentFood>
            {
                new() { CarbIntakeId = storedCarbIntakeId, Note = "Sandwich", Carbs = 45m },
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
    /// The bulk write dedups its insert set by legacy id keeping the first, so the food line must
    /// describe that same first treatment rather than a later duplicate's food type.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_DuplicateLegacyId_FoodLineFollowsTheInsertedTreatment()
    {
        // Arrange — emulate the bulk write's keep-first dedup by legacy id
        var carbIntakeId = Guid.CreateVersion7();
        _carbRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.CarbIntake>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.CarbIntake> records, WriteOrigin _, CancellationToken _) =>
            {
                var inserted = records.GroupBy(r => r.LegacyId!).Select(g => g.First()).ToList();
                foreach (var record in inserted)
                    record.Id = carbIntakeId;
                return inserted;
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
                It.Is<TreatmentFood>(f => f.CarbIntakeId == carbIntakeId && f.Note == "Sandwich"),
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
            x => x.UpsertStateSpanAsync(
                It.Is<StateSpan>(s => s.Category == StateSpanCategory.Profile),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _tempBasalRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.TempBasal>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().HaveCount(1);
    }
}
