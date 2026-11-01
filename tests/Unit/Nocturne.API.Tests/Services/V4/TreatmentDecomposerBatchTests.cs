using Nocturne.API.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Repositories;
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
using Nocturne.Tests.Shared.Mocks;
using Npgsql;
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
    private readonly Mock<IActiveProfileResolver> _activeProfileResolverMock;
    private readonly Mock<IPatientInsulinRepository> _insulinRepoMock;
    private readonly Mock<IBasalRateResolver> _basalRateResolverMock = new Mock<IBasalRateResolver>().AnswersBasalRates(_ => null);
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
        _tempBasalRepoMock = new Mock<ITempBasalRepository>().AnswersBatchFormsPerRecord();
        _stateSpanServiceMock = new Mock<IStateSpanService>().AnswersBatchFormsPerSpan();
        _treatmentFoodServiceMock = new Mock<ITreatmentFoodService>();
        _deviceServiceMock = new Mock<IDeviceService>().AnswersBatchFormsPerRecord();
        _activeProfileResolverMock = new Mock<IActiveProfileResolver>().AnswersInsulinContexts(_ => null);
        _insulinRepoMock = new Mock<IPatientInsulinRepository>();

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
        _tempBasalRepoMock
            .Setup(x => x.GetByLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

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

        _decomposer = CreateDecomposer(_deviceServiceMock.Object);
    }

    private TreatmentDecomposer CreateDecomposer(IDeviceService deviceService)
    {
        return new TreatmentDecomposer(
            _context.AsFactory(),
            _bolusRepoMock.Object,
            _tempBasalRepoMock.Object,
            _carbRepoMock.Object,
            _bgCheckRepoMock.Object,
            _noteRepoMock.Object,
            _deviceEventRepoMock.Object,
            _bolusCalcRepoMock.Object,
            _stateSpanServiceMock.Object,
            ProfileActivationTestDeps.Repository(_context),
            ProfileActivationTestDeps.Writer(_context),
            _treatmentFoodServiceMock.Object,
            deviceService,
            Mock.Of<IPatientDeviceStamper>(),
            _activeProfileResolverMock.Object,
            _insulinRepoMock.Object,
            TempBasalTestDeps.Rates(_basalRateResolverMock.Object),
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

    /// <summary>
    /// Each temp resolves against the schedule in force at its own start, as the single path does, so
    /// a batch spanning a profile edit gives each side its own rate.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_PercentTempBasals_ResolveEachAtItsOwnStart()
    {
        _basalRateResolverMock.AnswersBasalRates(mills => mills < 1700001000000 ? 0.8 : 1.2);

        var treatments = new List<Treatment>
        {
            new() { Id = "tb-percent-1", EventType = "Temp Basal", Mills = 1700000000000, Duration = 30, Percent = -50 },
            new() { Id = "tb-percent-2", EventType = "Temp Basal", Mills = 1700002000000, Duration = 30, Percent = 50 },
            new() { Id = "tb-aaps", EventType = "Temp Basal", Mills = 1700003000000, Duration = 30, Percent = -30, Rate = 0.7 },
            new() { Id = "tb-absolute", EventType = "Temp Basal", Mills = 1700004000000, Duration = 30, Absolute = 1.5 },
        };

        var result = await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        var temps = result.CreatedRecords.OfType<V4Models.TempBasal>().ToDictionary(t => t.LegacyId!);
        temps["tb-percent-1"].Rate.Should().BeApproximately(0.4, 1e-9);
        temps["tb-percent-1"].ScheduledRate.Should().Be(0.8);
        temps["tb-percent-1"].Percent.Should().Be(-50);
        temps["tb-percent-2"].Rate.Should().BeApproximately(1.8, 1e-9);
        temps["tb-percent-2"].ScheduledRate.Should().Be(1.2);
        temps["tb-aaps"].Rate.Should().Be(0.7);
        temps["tb-aaps"].Percent.Should().Be(-30);
        temps["tb-absolute"].Rate.Should().Be(1.5);
        temps["tb-absolute"].Percent.Should().BeNull();
        temps["tb-absolute"].ScheduledRate.Should().BeNull();
    }

    /// <summary>
    /// AAPS uploads a temp ahead of the profile switch that set it; a percent-only temp resolves after
    /// the batch's switches are written, and a temp carrying its rate keeps it.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_PercentTempBeforeSwitchInTheSameBatch_SeesTheSwitch()
    {
        _basalRateResolverMock.AnswersBasalRates(_ => _context.ProfileActivations.Any() ? 1.5 : 1.0);

        var treatments = new List<Treatment>
        {
            new() { Id = "tb-percent", EventType = "Temp Basal", Mills = 1700000060000, Duration = 30, Percent = -50 },
            new() { Id = "tb-aaps", EventType = "Temp Basal", Mills = 1700000120000, Duration = 30, Percent = 50, Rate = 1.65 },
            new() { Id = "ps-1", EventType = "Profile Switch", Mills = 1700000000000, Profile = "Default", Duration = 0, Percentage = 150 },
        };

        var result = await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        var temps = result.CreatedRecords.OfType<V4Models.TempBasal>().ToDictionary(t => t.LegacyId!);
        temps["tb-percent"].Rate.Should().BeApproximately(0.75, 1e-9);
        temps["tb-aaps"].Rate.Should().Be(1.65);
    }

    [Fact]
    public async Task DecomposeBatchAsync_PercentOnlyTempWithNoScheduleInForce_LeavesRateUnknown()
    {
        var treatments = new List<Treatment>
        {
            new() { Id = "tb-percent", EventType = "Temp Basal", Mills = 1700000000000, Duration = 30, Percent = -50 },
        };

        var result = await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        var temp = result.CreatedRecords.OfType<V4Models.TempBasal>().Single();
        temp.Rate.Should().BeNull();
        temp.ScheduledRate.Should().BeNull();
        temp.Percent.Should().Be(-50);
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

        // The calculation id travels with the bolus's own upsert, so no second write links it.
        _bolusRepoMock.Verify(
            x => x.UpdateAsync(It.IsAny<Guid>(), It.IsAny<V4Models.Bolus>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);

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
            x => x.AddManyAsync(
                It.Is<IReadOnlyCollection<TreatmentFood>>(lines => lines.Count(f =>
                    f.CarbIntakeId == mealCarbIntakeId && f.Note == "Sandwich" && f.Carbs == 45m) == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // The carb correction names no food, so it gets no line
        _treatmentFoodServiceMock.Verify(
            x => x.AddManyAsync(It.Is<IReadOnlyCollection<TreatmentFood>>(lines => lines.Count == 1), It.IsAny<CancellationToken>()),
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
            x => x.AddManyAsync(It.IsAny<IReadOnlyCollection<TreatmentFood>>(), It.IsAny<CancellationToken>()),
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
            x => x.AddManyAsync(
                It.Is<IReadOnlyCollection<TreatmentFood>>(lines => lines.Count(f => f.CarbIntakeId == carbIntakeId && f.Note == "Pizza") == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _treatmentFoodServiceMock.Verify(
            x => x.AddManyAsync(It.Is<IReadOnlyCollection<TreatmentFood>>(lines => lines.Count == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DecomposeBatchAsync_ProfileSwitch_WritesAProfileActivation()
    {
        var treatments = new List<Treatment>
        {
            new() { Id = "ps-1", EventType = "Profile Switch", Mills = 1700000000000, Profile = "Default", Duration = 0 },
        };

        var result = await _decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        result.CreatedRecords.OfType<V4Models.ProfileActivation>().Should().ContainSingle()
            .Which.LegacyId.Should().Be("ps-1");
        _stateSpanServiceMock.Verify(
            x => x.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _tempBasalRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.TempBasal>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().HaveCount(1);
    }

    [Fact]
    public async Task DecomposeBatchAsync_OnePumpManyTreatments_WritesLastSeenOnceWithTheNewestTimestamp()
    {
        var pumpId = Guid.NewGuid();
        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo
            .Setup(r => r.FindByIdentitiesAsync(DeviceRepositoryMatchers.Naming(V4Models.DeviceCategory.InsulinPump, "Omnipod DASH", "SN-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeviceRepositoryMatchers.Found(new V4Models.Device
            {
                Id = pumpId,
                Category = V4Models.DeviceCategory.InsulinPump,
                Type = "Omnipod DASH",
                Serial = "SN-1",
                FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1_000).UtcDateTime,
                LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1_000).UtcDateTime,
            }));
        var decomposer = CreateDecomposer(CreateDeviceService(deviceRepo));

        var treatments = Enumerable.Range(1, 5)
            .Select(n => new Treatment
            {
                Id = $"bolus-{n}",
                EventType = "Correction Bolus",
                Mills = 1_000 + n * 1_000,
                Insulin = 1,
                PumpType = "Omnipod DASH",
                PumpSerial = "SN-1",
            })
            .ToList();

        await decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        deviceRepo.Verify(
            r => r.WidenSeenWindowAsync(
                pumpId, DateTimeOffset.FromUnixTimeMilliseconds(6_000).UtcDateTime,
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
        deviceRepo.Verify(
            r => r.WidenSeenWindowAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DecomposeBatchAsync_BatchThrowsAndLastSeenWriteWouldFail_SurfacesTheBatchException()
    {
        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo
            .Setup(r => r.FindByIdentitiesAsync(DeviceRepositoryMatchers.Naming(V4Models.DeviceCategory.InsulinPump, "Omnipod DASH", "SN-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeviceRepositoryMatchers.Found(new V4Models.Device
            {
                Id = Guid.NewGuid(),
                Category = V4Models.DeviceCategory.InsulinPump,
                Type = "Omnipod DASH",
                Serial = "SN-1",
                FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1_000).UtcDateTime,
                LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1_000).UtcDateTime,
            }));
        deviceRepo
            .Setup(r => r.FindByIdentitiesAsync(DeviceRepositoryMatchers.Naming(V4Models.DeviceCategory.InsulinPump, "Omnipod DASH", "SN-2"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("batch failure"));
        deviceRepo
            .Setup(r => r.WidenSeenWindowAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("flush failure"));
        var decomposer = CreateDecomposer(CreateDeviceService(deviceRepo));

        var treatments = new List<Treatment>
        {
            new() { Id = "bolus-1", EventType = "Correction Bolus", Mills = 2_000, Insulin = 1, PumpType = "Omnipod DASH", PumpSerial = "SN-1" },
            new() { Id = "bolus-2", EventType = "Correction Bolus", Mills = 3_000, Insulin = 1, PumpType = "Omnipod DASH", PumpSerial = "SN-2" },
        };

        var act = () => decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("batch failure");
    }

    private async Task<List<V4Models.TempBasal>> BatchPutStoredTempBasalAsync(
        V4Models.TempBasal stored, V4Models.TempBasalOrigin echoedOrigin, V4Models.TempBasalOriginEvidence echoedEvidence)
    {
        var put = Nocturne.Infrastructure.Data.Mappers.TempBasalToTreatmentMapper.ToTreatment(stored);
        put.AdditionalProperties!["basalOrigin"] = echoedOrigin.ToString();
        put.AdditionalProperties["originEvidence"] = echoedEvidence.ToString();
        put = System.Text.Json.JsonSerializer.Deserialize<Treatment>(System.Text.Json.JsonSerializer.Serialize(put))!;
        put.Id = stored.LegacyId;
        _tempBasalRepoMock
            .Setup(r => r.GetByLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([stored]);
        List<V4Models.TempBasal> written = [];
        _tempBasalRepoMock
            .Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.TempBasal>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<V4Models.TempBasal> tbs, WriteOrigin _, CancellationToken _) => written = [.. tbs])
            .ReturnsAsync((IEnumerable<V4Models.TempBasal> tbs, WriteOrigin _, CancellationToken _) => [.. tbs]);

        await _decomposer.DecomposeBatchAsync([put], WriteOrigin.Live);
        return written;
    }

    private static V4Models.TempBasal StoredTempBasal(
        V4Models.TempBasalOrigin origin, V4Models.TempBasalOriginEvidence evidence) => new()
    {
        Id = Guid.CreateVersion7(),
        LegacyId = "tb-batch-origin",
        StartTimestamp = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc),
        EndTimestamp = new DateTime(2026, 9, 20, 12, 30, 0, DateTimeKind.Utc),
        Rate = 0.4,
        Origin = origin,
        OriginEvidence = evidence,
        App = "Trio",
    };

    [Theory]
    [InlineData(V4Models.TempBasalOriginEvidence.Snapshot)]
    [InlineData(V4Models.TempBasalOriginEvidence.Uploader)]
    public async Task DecomposeBatchAsync_ChangingTheOriginWhileEchoingEvidence_StatesTheNewOrigin(
        V4Models.TempBasalOriginEvidence echoed)
    {
        var stored = StoredTempBasal(V4Models.TempBasalOrigin.Algorithm, echoed);

        var written = await BatchPutStoredTempBasalAsync(stored, V4Models.TempBasalOrigin.Manual, echoed);

        var tempBasal = written.Should().ContainSingle().Subject;
        tempBasal.Origin.Should().Be(V4Models.TempBasalOrigin.Manual);
        tempBasal.OriginEvidence.Should().Be(V4Models.TempBasalOriginEvidence.Reported);
    }

    [Theory]
    [InlineData(V4Models.TempBasalOriginEvidence.Snapshot)]
    [InlineData(V4Models.TempBasalOriginEvidence.Uploader)]
    public async Task DecomposeBatchAsync_EchoingTheStoredOrigin_KeepsItAndItsEvidence(
        V4Models.TempBasalOriginEvidence echoed)
    {
        var stored = StoredTempBasal(V4Models.TempBasalOrigin.Algorithm, echoed);

        var written = await BatchPutStoredTempBasalAsync(stored, V4Models.TempBasalOrigin.Algorithm, echoed);

        var tempBasal = written.Should().ContainSingle().Subject;
        tempBasal.Origin.Should().Be(V4Models.TempBasalOrigin.Algorithm);
        tempBasal.OriginEvidence.Should().Be(echoed);
    }

    /// <summary>
    /// Once the bolus of a wizard is refused, nothing more of the wizard is written; the other
    /// treatment the bolus table held is written by the rerun.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_ARefusedBolus_WritesNothingMoreOfItsTreatment()
    {
        RefuseWhere<V4Models.Bolus>(_bolusRepoMock, b => b.LegacyId == "wizard");
        var decomposition = new TreatmentDecomposition();

        await _decomposer.DecomposeBatchAsync(
        [
            new Treatment { Id = "wizard", EventType = "Bolus Wizard", Insulin = 4, Carbs = 40, FoodType = "toast", Mills = 1_790_000_000_000 },
            new Treatment { Id = "other", EventType = "Meal Bolus", Insulin = 2, Carbs = 20, Mills = 1_790_000_300_000 },
        ], WriteOrigin.Live, default, decomposition);

        decomposition.Rejected.Keys.Should().Equal(0);
        decomposition.StoredRecords.OfType<V4Models.CarbIntake>().Select(c => c.LegacyId).Should().Equal("other");
        decomposition.StoredRecords.OfType<V4Models.Bolus>().Select(b => b.LegacyId).Should().Equal("other");
        _treatmentFoodServiceMock.Verify(
            s => s.AddManyAsync(It.IsAny<IReadOnlyCollection<TreatmentFood>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DecomposeBatchAsync_ARefusedPumpSuspend_OpensNoSuspension()
    {
        RefuseWhere<V4Models.DeviceEvent>(_deviceEventRepoMock, e => e.LegacyId == "suspend");
        var decomposition = new TreatmentDecomposition();

        await _decomposer.DecomposeBatchAsync(
        [
            new Treatment { Id = "suspend", EventType = "Pump Suspend", Mills = 1_790_000_000_000 },
            new Treatment { Id = "site", EventType = "Site Change", Mills = 1_790_000_300_000 },
        ], WriteOrigin.Live, default, decomposition);

        decomposition.Rejected.Keys.Should().Equal(0);
        decomposition.StoredRecords.OfType<V4Models.DeviceEvent>().Select(e => e.LegacyId).Should().Equal("site");
        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A meal's bolus and its carbs sent under a legacy id of their own share a group; refusing the
    /// bolus rejects the bolus's treatment, not the last of its group.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_ARefusedMealBolus_RejectsItsOwnTreatmentAndNotItsSeparatelyKeyedCarbs()
    {
        var group = Guid.CreateVersion7();
        _bolusRepoMock
            .Setup(r => r.GetByLegacyIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new V4Models.Bolus { Id = Guid.CreateVersion7(), LegacyId = "meal-insulin", CorrelationId = group, Insulin = 3 }]);
        _carbRepoMock
            .Setup(r => r.GetByLegacyIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new V4Models.CarbIntake { Id = Guid.CreateVersion7(), LegacyId = "meal-carbs", CorrelationId = group, Carbs = 30 }]);
        RefuseWhere<V4Models.Bolus>(_bolusRepoMock, b => b.LegacyId == "meal-insulin");
        var decomposition = new TreatmentDecomposition();

        await _decomposer.DecomposeBatchAsync(
        [
            new Treatment { Id = "meal-insulin", EventType = "Meal Bolus", Insulin = 3, Mills = 1_790_000_000_000 },
            new Treatment { Id = "meal-carbs", EventType = "Meal Bolus", Carbs = 30, Mills = 1_790_000_000_000 },
        ], WriteOrigin.Live, default, decomposition);

        decomposition.Groups.Should().Equal(group, group);
        decomposition.Rejected.Keys.Should().Equal(0);
        decomposition.StoredRecords.OfType<V4Models.CarbIntake>().Select(c => c.LegacyId).Should().Equal("meal-carbs");
    }

    /// <summary>
    /// A span refused in the bulk write sends the spans one at a time; a failure that ends the batch
    /// part way through them keeps the spans written before it, so the batch stops at the treatment
    /// whose span failed.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_AFailureWhileWritingSpansOneAtATime_KeepsTheSpansWrittenBeforeIt()
    {
        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpansAsync(It.IsAny<IReadOnlyList<StateSpan>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<StateSpan> spans, CancellationToken _) => spans switch
            {
                [{ OriginalId: "target-1" } span] => (IReadOnlyList<StateSpanUpsert>)[new StateSpanUpsert(span, UpsertOutcome.Created)],
                [{ OriginalId: "target-2" }] => throw new TimeoutException(),
                _ => throw Refusal(),
            });
        var decomposition = new TreatmentDecomposition();

        var act = () => _decomposer.DecomposeBatchAsync(
            [.. Enumerable.Range(0, 3).Select(i => new Treatment
            {
                Id = $"target-{i}", EventType = "Temporary Target", Duration = 30, TargetTop = 120, TargetBottom = 100,
                Mills = 1_790_000_000_000 + i * 3_600_000L,
            })], WriteOrigin.Live, default, decomposition);

        await act.Should().ThrowAsync<TimeoutException>();
        decomposition.Rejected.Keys.Should().Equal(0);
        decomposition.Of(1).StoredRecords.OfType<StateSpan>().Select(s => s.OriginalId).Should().Equal("target-1");
        decomposition.Wrote.Should().Equal(false, true, false);
        decomposition.Of(2).StoredRecords.Should().BeEmpty();
        decomposition.StoresNothing(2).Should().BeFalse();
    }

    /// <summary>Makes <paramref name="repository"/> refuse, for its data, any write holding a record that matches <paramref name="refused"/>.</summary>
    private static void RefuseWhere<TRecord>(Mock repository, Func<TRecord, bool> refused)
        where TRecord : class =>
        repository.As<IBulkUpsertRepository<TRecord>>()
            .Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<TRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<TRecord> records, WriteOrigin _, CancellationToken _) =>
                records.Any(refused) ? throw Refusal() : (BulkWrite<TRecord>)[.. records]);

    private static DbUpdateException Refusal() =>
        new("refused", new PostgresException("value too long for type character varying(255)", "ERROR", "ERROR", "22001"));

    private static DeviceService CreateDeviceService(Mock<IDeviceRepository> deviceRepo)
    {
        var patientDevices = new Mock<IPatientDeviceRepository>();
        patientDevices
            .Setup(r => r.GetByDeviceIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return new DeviceService(deviceRepo.Object, patientDevices.Object, MockTenantAccessor.Create().Object, DeviceDateClockDoubles.Utc());
    }
}
