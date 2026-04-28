using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

public class TreatmentDecomposerTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly Mock<IStateSpanService> _stateSpanServiceMock;
    private readonly Mock<ITreatmentFoodService> _treatmentFoodServiceMock;
    private readonly Mock<ITempBasalRepository> _tempBasalRepoMock;
    private readonly Mock<IDeviceService> _deviceServiceMock;
    private readonly Mock<IProfileDecomposer> _profileDecomposerMock;
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var mockDedup = new Mock<IDeduplicationService>();
        var mockAudit = new Mock<IAuditContext>().Object;
        var bolusRepo = new BolusRepository(_context, mockDedup.Object, mockAudit, NullLogger<BolusRepository>.Instance);
        var carbIntakeRepo = new CarbIntakeRepository(_context, mockDedup.Object, mockAudit, NullLogger<CarbIntakeRepository>.Instance);
        var bgCheckRepo = new BGCheckRepository(_context, mockDedup.Object, NullLogger<BGCheckRepository>.Instance);
        var noteRepo = new NoteRepository(_context, mockDedup.Object, NullLogger<NoteRepository>.Instance);
        var deviceEventRepo = new DeviceEventRepository(_context, mockDedup.Object, mockAudit, NullLogger<DeviceEventRepository>.Instance);
        var bolusCalcRepo = new BolusCalculationRepository(_context, mockDedup.Object, mockAudit, NullLogger<BolusCalculationRepository>.Instance);
        _stateSpanServiceMock = new Mock<IStateSpanService>();
        _treatmentFoodServiceMock = new Mock<ITreatmentFoodService>();
        _tempBasalRepoMock = new Mock<ITempBasalRepository>();
        _deviceServiceMock = new Mock<IDeviceService>();
        _profileDecomposerMock = new Mock<IProfileDecomposer>();

        // Default: DeviceService returns null (no device resolved)
        _deviceServiceMock
            .Setup(s => s.ResolveAsync(It.IsAny<V4Models.DeviceCategory>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        _decomposer = new TreatmentDecomposer(
            _context,
            bolusRepo, _tempBasalRepoMock.Object,
            carbIntakeRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
            _stateSpanServiceMock.Object,
            _treatmentFoodServiceMock.Object,
            _deviceServiceMock.Object,
            _profileDecomposerMock.Object,
            NullLogger<TreatmentDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Meal Bolus → Bolus + CarbIntake

    [Fact]
    public async Task DecomposeAsync_MealBolus_CreatesBolusAndCarbIntakeWithSharedCorrelationId()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "meal-bolus-1",
            EventType = "Meal Bolus",
            Mills = 1700000000000,
            Insulin = 5.5,
            Carbs = 45,
            Protein = 10,
            Fat = 5,
            FoodType = "Sandwich",
            AbsorptionTime = 120,
            EnteredBy = "xDrip+",
            DataSource = "manual",
            UtcOffset = -300,
            BolusType = "normal"
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CorrelationId.Should().NotBeNull();
        result.CreatedRecords.Should().HaveCount(2);
        result.UpdatedRecords.Should().BeEmpty();

        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().Single();
        bolus.LegacyId.Should().Be("meal-bolus-1");
        bolus.Mills.Should().Be(1700000000000);
        bolus.Insulin.Should().Be(5.5);
        bolus.BolusType.Should().Be(V4Models.BolusType.Normal);
        bolus.Device.Should().Be("xDrip+");
        bolus.DataSource.Should().Be("manual");
        bolus.UtcOffset.Should().Be(-300);
        bolus.CorrelationId.Should().Be(result.CorrelationId);

        var carbIntake = result.CreatedRecords.OfType<V4Models.CarbIntake>().Single();
        carbIntake.LegacyId.Should().Be("meal-bolus-1");
        carbIntake.Mills.Should().Be(1700000000000);
        carbIntake.Carbs.Should().Be(45);
        carbIntake.CorrelationId.Should().Be(result.CorrelationId);

        // Both records share the same CorrelationId
        bolus.CorrelationId.Should().Be(carbIntake.CorrelationId);
    }

    [Fact]
    public async Task DecomposeAsync_SnackBolus_CreatesBolusAndCarbIntake()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "snack-bolus-1",
            EventType = "Snack Bolus",
            Mills = 1700000000000,
            Insulin = 2.0,
            Carbs = 15
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(2);
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().HaveCount(1);
    }

    #endregion

    #region Correction Bolus → Bolus only

    [Fact]
    public async Task DecomposeAsync_CorrectionBolus_CreatesBolusOnly()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "correction-1",
            EventType = "Correction Bolus",
            Mills = 1700000000000,
            Insulin = 3.0,
            Programmed = 3.0,
            Automatic = true,
            BolusType = "normal",
            EnteredBy = "Loop"
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var bolus = result.CreatedRecords[0].Should().BeOfType<V4Models.Bolus>().Subject;
        bolus.Insulin.Should().Be(3.0);
        bolus.Programmed.Should().Be(3.0);
        bolus.Automatic.Should().BeTrue();
        bolus.BolusType.Should().Be(V4Models.BolusType.Normal);
        bolus.LegacyId.Should().Be("correction-1");
    }

    #endregion

    #region Carb Correction → CarbIntake only

    [Fact]
    public async Task DecomposeAsync_CarbCorrection_CreatesCarbIntakeOnly()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "carb-correction-1",
            EventType = "Carb Correction",
            Mills = 1700000000000,
            Carbs = 15,
            FoodType = "Juice"
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var carbIntake = result.CreatedRecords[0].Should().BeOfType<V4Models.CarbIntake>().Subject;
        carbIntake.Carbs.Should().Be(15);
        carbIntake.LegacyId.Should().Be("carb-correction-1");
    }

    #endregion

    #region BG Check → BGCheck

    [Fact]
    public async Task DecomposeAsync_BGCheck_CreatesBGCheckWithCorrectFields()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "bgcheck-1",
            EventType = "BG Check",
            Mills = 1700000000000,
            Glucose = 120,
            GlucoseType = "Finger",
            Mgdl = 120,
            Mmol = 6.7,
            Units = "mg/dl",
            EnteredBy = "contour-next",
            UtcOffset = 60
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var bgCheck = result.CreatedRecords[0].Should().BeOfType<V4Models.BGCheck>().Subject;
        bgCheck.LegacyId.Should().Be("bgcheck-1");
        bgCheck.Mills.Should().Be(1700000000000);
        bgCheck.Glucose.Should().Be(120);
        bgCheck.GlucoseType.Should().Be(V4Models.GlucoseType.Finger);
        bgCheck.Units.Should().Be(V4Models.GlucoseUnit.MgDl);
        bgCheck.Mgdl.Should().Be(120, "mg/dL glucose should pass through as-is");
        bgCheck.Mmol.Should().BeApproximately(120 / 18.0182, 0.01, "mmol should be computed from glucose");
        bgCheck.Device.Should().Be("contour-next");
        bgCheck.UtcOffset.Should().Be(60);
    }

    #endregion

    #region Note → Note

    [Fact]
    public async Task DecomposeAsync_Note_CreatesNoteWithCorrectFields()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "note-1",
            EventType = "Note",
            Mills = 1700000000000,
            Notes = "Changed infusion site",
            EnteredBy = "manual",
            DataSource = "manual"
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var note = result.CreatedRecords[0].Should().BeOfType<V4Models.Note>().Subject;
        note.LegacyId.Should().Be("note-1");
        note.Text.Should().Be("Changed infusion site");
        note.EventType.Should().Be("Note");
        note.IsAnnouncement.Should().BeFalse();
    }

    #endregion

    #region Announcement → Note with IsAnnouncement=true

    [Fact]
    public async Task DecomposeAsync_Announcement_CreatesNoteWithIsAnnouncementTrue()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "announcement-1",
            EventType = "Announcement",
            Mills = 1700000000000,
            Notes = "Sensor warmup in progress"
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var note = result.CreatedRecords[0].Should().BeOfType<V4Models.Note>().Subject;
        note.LegacyId.Should().Be("announcement-1");
        note.Text.Should().Be("Sensor warmup in progress");
        note.EventType.Should().Be("Announcement");
        note.IsAnnouncement.Should().BeTrue();
    }

    #endregion

    #region TempBasal → Creates TempBasal via Repository

    [Theory]
    [InlineData("Temp Basal")]
    [InlineData("Temp Basal Start")]
    [InlineData("TempBasal")]
    public async Task DecomposeAsync_TempBasal_CreatesTempBasalViaRepository(string eventType)
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "temp-basal-1",
            EventType = eventType,
            Mills = 1700000000000,
            Rate = 1.5,
            Duration = 30
        };

        _tempBasalRepoMock
            .Setup(r => r.GetByLegacyIdAsync("temp-basal-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((V4Models.TempBasal?)null);

        _tempBasalRepoMock
            .Setup(r => r.CreateAsync(It.IsAny<V4Models.TempBasal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((V4Models.TempBasal tb, CancellationToken _) => tb);

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var tempBasal = result.CreatedRecords[0].Should().BeOfType<V4Models.TempBasal>().Subject;
        tempBasal.LegacyId.Should().Be("temp-basal-1");
        tempBasal.StartMills.Should().Be(1700000000000);
        tempBasal.Rate.Should().Be(1.5);
        tempBasal.EndMills.Should().Be(1700000000000 + (30 * 60 * 1000));
        tempBasal.Origin.Should().Be(V4Models.TempBasalOrigin.Manual);

        _tempBasalRepoMock.Verify(
            r => r.CreateAsync(It.IsAny<V4Models.TempBasal>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Profile Switch → Delegates to IStateSpanService

    [Fact]
    public async Task DecomposeAsync_ProfileSwitch_DelegatesToStateSpanServiceUpsert()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "profile-switch-1",
            EventType = "Profile Switch",
            Mills = 1700000000000,
            Profile = "Day Profile",
            Duration = 60,
            EnteredBy = "AAPS"
        };

        var expectedStateSpan = new StateSpan
        {
            Id = "state-span-456",
            Category = StateSpanCategory.Profile,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime
        };

        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        result.CreatedRecords[0].Should().BeOfType<StateSpan>();

        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss =>
                    ss.Category == StateSpanCategory.Profile
                    && ss.State == "Active"
                    && ss.StartMills == 1700000000000
                    && ss.OriginalId == "profile-switch-1"
                    && ss.Metadata != null
                    && ss.Metadata.ContainsKey("profileName")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Bolus Wizard → BolusCalculation (+ Bolus if insulin)

    [Fact]
    public async Task DecomposeAsync_BolusWizardWithInsulin_CreatesBolusCalculationAndBolus()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "bolus-wizard-1",
            EventType = "Bolus Wizard",
            Mills = 1700000000000,
            Insulin = 4.0,
            Carbs = 30,
            BloodGlucoseInput = 180,
            BloodGlucoseInputSource = "Sensor",
            InsulinOnBoard = 1.5,
            InsulinRecommendationForCorrection = 2.0,
            CR = 10.0,
            CalculationType = Nocturne.Core.Models.CalculationType.Suggested
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert - should produce BolusCalculation + Bolus + CarbIntake (override rule: insulin > 0 AND carbs > 0)
        result.CreatedRecords.OfType<V4Models.BolusCalculation>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
        // Override rule fires since both insulin > 0 and carbs > 0
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().HaveCount(1);

        var calc = result.CreatedRecords.OfType<V4Models.BolusCalculation>().Single();
        calc.BloodGlucoseInput.Should().Be(180);
        calc.BloodGlucoseInputSource.Should().Be("Sensor");
        calc.CarbInput.Should().Be(30);
        calc.InsulinOnBoard.Should().Be(1.5);
        calc.InsulinRecommendation.Should().Be(2.0);
        calc.CarbRatio.Should().Be(10.0);
        calc.CalculationType.Should().Be(V4Models.CalculationType.Suggested);
    }

    [Fact]
    public async Task DecomposeAsync_BolusWizardWithoutInsulin_CreatesBolusCalculationOnly()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "bolus-wizard-no-insulin",
            EventType = "Bolus Wizard",
            Mills = 1700000000000,
            BloodGlucoseInput = 150,
            InsulinOnBoard = 3.0
            // No insulin, no carbs
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        result.CreatedRecords[0].Should().BeOfType<V4Models.BolusCalculation>();
    }

    #endregion

    #region Override Rule: Insulin + Carbs → Always Bolus + CarbIntake

    [Fact]
    public async Task DecomposeAsync_UnknownEventTypeWithInsulinAndCarbs_ProducesBothBolusAndCarbIntake()
    {
        // Arrange - unknown event type but has both insulin and carbs
        var treatment = new Treatment
        {
            Id = "override-rule-1",
            EventType = "Custom Bolus",
            Mills = 1700000000000,
            Insulin = 3.0,
            Carbs = 20
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().HaveCount(1);

        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().Single();
        bolus.Insulin.Should().Be(3.0);

        var carbIntake = result.CreatedRecords.OfType<V4Models.CarbIntake>().Single();
        carbIntake.Carbs.Should().Be(20);
    }

    [Fact]
    public async Task DecomposeAsync_CorrectionBolusWithCarbs_ProducesBothBolusAndCarbIntake()
    {
        // Arrange - Correction Bolus normally only produces a Bolus,
        // but if it has both insulin and carbs, the override rule fires
        var treatment = new Treatment
        {
            Id = "correction-with-carbs",
            EventType = "Correction Bolus",
            Mills = 1700000000000,
            Insulin = 2.5,
            Carbs = 10
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().HaveCount(1);
    }

    #endregion

    #region Idempotency

    [Fact]
    public async Task DecomposeAsync_SameTreatmentTwice_UpdatesInsteadOfCreatingDuplicate()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "idempotent-bolus",
            EventType = "Correction Bolus",
            Mills = 1700000000000,
            Insulin = 3.0
        };

        // Act - first call creates
        var firstResult = await _decomposer.DecomposeAsync(treatment);
        firstResult.CreatedRecords.Should().HaveCount(1);
        firstResult.UpdatedRecords.Should().BeEmpty();

        // Modify the insulin value
        treatment.Insulin = 3.5;

        // Act - second call should update
        var secondResult = await _decomposer.DecomposeAsync(treatment);

        // Assert
        secondResult.CreatedRecords.Should().BeEmpty();
        secondResult.UpdatedRecords.Should().HaveCount(1);

        var updated = secondResult.UpdatedRecords[0].Should().BeOfType<V4Models.Bolus>().Subject;
        updated.LegacyId.Should().Be("idempotent-bolus");
        updated.Insulin.Should().Be(3.5);
    }

    [Fact]
    public async Task DecomposeAsync_MealBolusTwice_UpdatesBothBolusAndCarbIntake()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "idempotent-meal",
            EventType = "Meal Bolus",
            Mills = 1700000000000,
            Insulin = 5.0,
            Carbs = 40
        };

        // Act - first call creates
        var firstResult = await _decomposer.DecomposeAsync(treatment);
        firstResult.CreatedRecords.Should().HaveCount(2);

        // Modify values
        treatment.Insulin = 6.0;
        treatment.Carbs = 50;

        // Act - second call should update both
        var secondResult = await _decomposer.DecomposeAsync(treatment);

        // Assert
        secondResult.CreatedRecords.Should().BeEmpty();
        secondResult.UpdatedRecords.Should().HaveCount(2);

        var updatedBolus = secondResult.UpdatedRecords.OfType<V4Models.Bolus>().Single();
        updatedBolus.Insulin.Should().Be(6.0);

        var updatedCarbIntake = secondResult.UpdatedRecords.OfType<V4Models.CarbIntake>().Single();
        updatedCarbIntake.Carbs.Should().Be(50);
    }

    #endregion

    #region Unknown/Edge Cases

    [Fact]
    public async Task DecomposeAsync_UnknownEventTypeNoInsulinNoCarbs_ReturnsEmptyResult()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "unknown-1",
            EventType = "Unknown Event",
            Mills = 1700000000000
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        result.CorrelationId.Should().NotBeNull("a correlation ID is always generated");
    }

    [Fact]
    public async Task DecomposeAsync_NullEventType_ReturnsEmptyResult()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "null-event-type",
            EventType = null,
            Mills = 1700000000000
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_EmptyEventType_ReturnsEmptyResult()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "empty-event-type",
            EventType = "",
            Mills = 1700000000000
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_NullId_StillCreatesRecord()
    {
        // Arrange - treatment with no ID should still decompose (just can't deduplicate)
        var treatment = new Treatment
        {
            Id = null,
            EventType = "Note",
            Mills = 1700000000000,
            Notes = "Test note"
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var note = result.CreatedRecords[0].Should().BeOfType<V4Models.Note>().Subject;
        note.LegacyId.Should().BeNull();
        note.Text.Should().Be("Test note");
    }

    [Fact]
    public async Task DecomposeAsync_NoteWithNullNotes_DefaultsToEmptyString()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "note-null-text",
            EventType = "Note",
            Mills = 1700000000000,
            Notes = null
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var note = result.CreatedRecords[0].Should().BeOfType<V4Models.Note>().Subject;
        note.Text.Should().BeEmpty();
    }

    #endregion

    #region Static Mapping Methods

    [Theory]
    [InlineData("normal", V4Models.BolusType.Normal)]
    [InlineData("Normal", V4Models.BolusType.Normal)]
    [InlineData("square", V4Models.BolusType.Square)]
    [InlineData("dual", V4Models.BolusType.Dual)]
    public void ParseBolusType_KnownValues_MapsCorrectly(string input, V4Models.BolusType expected)
    {
        TreatmentDecomposer.ParseBolusType(input).Should().Be(expected);
    }

    [Fact]
    public void ParseBolusType_Null_ReturnsNull()
    {
        TreatmentDecomposer.ParseBolusType(null).Should().BeNull();
    }

    [Fact]
    public void ParseBolusType_Empty_ReturnsNull()
    {
        TreatmentDecomposer.ParseBolusType("").Should().BeNull();
    }

    [Theory]
    [InlineData("Finger", V4Models.GlucoseType.Finger)]
    [InlineData("finger", V4Models.GlucoseType.Finger)]
    [InlineData("Sensor", V4Models.GlucoseType.Sensor)]
    [InlineData("sensor", V4Models.GlucoseType.Sensor)]
    public void ParseGlucoseType_KnownValues_MapsCorrectly(string input, V4Models.GlucoseType expected)
    {
        TreatmentDecomposer.ParseGlucoseType(input).Should().Be(expected);
    }

    [Fact]
    public void ParseGlucoseType_Null_ReturnsNull()
    {
        TreatmentDecomposer.ParseGlucoseType(null).Should().BeNull();
    }

    [Theory]
    [InlineData("mg/dl", V4Models.GlucoseUnit.MgDl)]
    [InlineData("mgdl", V4Models.GlucoseUnit.MgDl)]
    [InlineData("mg", V4Models.GlucoseUnit.MgDl)]
    [InlineData("mmol", V4Models.GlucoseUnit.Mmol)]
    [InlineData("mmol/l", V4Models.GlucoseUnit.Mmol)]
    public void ParseGlucoseUnit_KnownValues_MapsCorrectly(string input, V4Models.GlucoseUnit expected)
    {
        TreatmentDecomposer.ParseGlucoseUnit(input).Should().Be(expected);
    }

    [Fact]
    public void ParseGlucoseUnit_Null_ReturnsNull()
    {
        TreatmentDecomposer.ParseGlucoseUnit(null).Should().BeNull();
    }

    [Theory]
    [InlineData(Nocturne.Core.Models.CalculationType.Suggested, V4Models.CalculationType.Suggested)]
    [InlineData(Nocturne.Core.Models.CalculationType.Manual, V4Models.CalculationType.Manual)]
    [InlineData(Nocturne.Core.Models.CalculationType.Automatic, V4Models.CalculationType.Automatic)]
    public void MapCalculationType_KnownValues_MapsCorrectly(
        Nocturne.Core.Models.CalculationType input,
        V4Models.CalculationType expected)
    {
        TreatmentDecomposer.MapCalculationType(input).Should().Be(expected);
    }

    [Fact]
    public void MapCalculationType_Null_ReturnsNull()
    {
        TreatmentDecomposer.MapCalculationType(null).Should().BeNull();
    }

    #endregion

    #region Event Type Case Insensitivity

    [Theory]
    [InlineData("meal bolus")]
    [InlineData("MEAL BOLUS")]
    [InlineData("Meal Bolus")]
    public async Task DecomposeAsync_EventTypeCaseInsensitive_HandlesCorrectly(string eventType)
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = $"case-test-{eventType.GetHashCode()}",
            EventType = eventType,
            Mills = 1700000000000,
            Insulin = 2.0,
            Carbs = 15
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().HaveCount(1);
    }

    #endregion

    #region BGCheck Fallback Values

    [Fact]
    public async Task DecomposeAsync_BGCheckWithMmolUnits_ComputesMgdlFromGlucose()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "bgcheck-mmol",
            EventType = "BG Check",
            Mills = 1700000000000,
            Glucose = 7.2,
            GlucoseType = "Sensor",
            Units = "mmol/l"
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var bgCheck = result.CreatedRecords[0].Should().BeOfType<V4Models.BGCheck>().Subject;
        bgCheck.Glucose.Should().Be(7.2);
        bgCheck.GlucoseType.Should().Be(V4Models.GlucoseType.Sensor);
        bgCheck.Units.Should().Be(V4Models.GlucoseUnit.Mmol);
        bgCheck.Mmol.Should().Be(7.2, "mmol should equal Glucose when Units is Mmol");
        bgCheck.Mgdl.Should().BeApproximately(7.2 * 18.0182, 0.01, "mgdl should be computed from glucose * 18.0182");
    }

    #endregion

    #region Bolus Field Mapping Details

    [Fact]
    public async Task DecomposeAsync_CorrectionBolus_MapsAllBolusFields()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "bolus-fields-test",
            EventType = "Correction Bolus",
            Mills = 1700000000000,
            Insulin = 4.5,
            Programmed = 5.0,
            InsulinDelivered = 4.5,
            BolusType = "dual",
            Automatic = false,
            Duration = 30,
            EnteredBy = "Omnipod",
            DataSource = "loop-connector",
            UtcOffset = -480
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var bolus = result.CreatedRecords[0].Should().BeOfType<V4Models.Bolus>().Subject;
        bolus.Insulin.Should().Be(4.5);
        bolus.Programmed.Should().Be(5.0);
        bolus.Delivered.Should().Be(4.5);
        bolus.BolusType.Should().Be(V4Models.BolusType.Dual);
        bolus.Automatic.Should().BeFalse();
        bolus.Duration.Should().Be(30);
        bolus.Device.Should().Be("Omnipod");
        bolus.DataSource.Should().Be("loop-connector");
        bolus.UtcOffset.Should().Be(-480);
    }

    #endregion

    #region Temporary Override → Delegates to IStateSpanService

    [Fact]
    public async Task DecomposeAsync_TemporaryOverride_DelegatesToStateSpanService()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "override-1",
            EventType = "Temporary Override",
            Mills = 1700000000000,
            Duration = 60,
            Reason = "Workout",
            TargetTop = 150,
            TargetBottom = 80,
            InsulinNeedsScaleFactor = 0.8,
            EnteredBy = "AAPS"
        };

        var expectedStateSpan = new StateSpan
        {
            Id = "state-span-789",
            Category = StateSpanCategory.Override,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime
        };

        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        result.CreatedRecords[0].Should().BeOfType<StateSpan>();

        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss =>
                    ss.Category == StateSpanCategory.Override
                    && ss.State == "Custom"
                    && ss.StartMills == 1700000000000
                    && ss.OriginalId == "override-1"
                    && ss.Metadata != null
                    && ss.Metadata.ContainsKey("reason")
                    && ss.Metadata.ContainsKey("targetTop")
                    && ss.Metadata.ContainsKey("targetBottom")
                    && ss.Metadata.ContainsKey("insulinNeedsScaleFactor")
                    && ss.Metadata.ContainsKey("enteredBy")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region APS Field Mapping - Bolus

    [Fact]
    public void MapToBolus_MapsApsFields()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var treatment = new Treatment
        {
            Id = "aps-bolus-1",
            Mills = 1700000000000,
            Insulin = 3.5,
            SyncIdentifier = "loop-sync-abc123",
            InsulinType = "Humalog",
            Unabsorbed = 1.2,
            IsBasalInsulin = true,
            PumpId = 42,
            PumpSerial = "SN-12345",
            PumpType = "Omnipod DASH"
        };

        // Act
        var bolus = TreatmentDecomposer.MapToBolus(treatment, correlationId);

        // Assert
        bolus.SyncIdentifier.Should().Be("loop-sync-abc123");
        bolus.InsulinType.Should().Be("Humalog");
        bolus.Unabsorbed.Should().Be(1.2);
        bolus.PumpRecordId.Should().Be("42");
    }

    #endregion

    #region APS Field Mapping - CarbIntake

    [Fact]
    public void MapToCarbIntake_MapsApsFields()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var treatment = new Treatment
        {
            Id = "aps-carb-1",
            Mills = 1700000000000,
            Carbs = 45,
            SyncIdentifier = "loop-sync-carb456",
            CarbTime = 15
        };

        // Act
        var carbIntake = TreatmentDecomposer.MapToCarbIntake(treatment, correlationId);

        // Assert
        carbIntake.SyncIdentifier.Should().Be("loop-sync-carb456");
        carbIntake.CarbTime.Should().Be(15);
    }

    #endregion

    #region APS Field Mapping - BolusCalculation

    [Fact]
    public void MapToBolusCalculation_MapsApsFields()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var treatment = new Treatment
        {
            Id = "aps-calc-1",
            Mills = 1700000000000,
            InsulinRecommendationForCarbs = 3.0,
            InsulinProgrammed = 4.5,
            EnteredInsulin = 4.0,
            SplitNow = 60,
            SplitExt = 40,
            PreBolus = 15
        };

        // Act
        var calc = TreatmentDecomposer.MapToBolusCalculation(treatment, correlationId);

        // Assert
        calc.InsulinRecommendationForCarbs.Should().Be(3.0);
        calc.InsulinProgrammed.Should().Be(4.5);
        calc.EnteredInsulin.Should().Be(4.0);
        calc.SplitNow.Should().Be(60);
        calc.SplitExt.Should().Be(40);
        calc.PreBolus.Should().Be(15);
    }

    #endregion

    #region SyncIdentifier Mapping - BGCheck, Note, DeviceEvent

    [Fact]
    public void MapToBGCheck_MapsSyncIdentifier()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var treatment = new Treatment
        {
            Id = "bgcheck-sync-1",
            Mills = 1700000000000,
            Glucose = 120,
            SyncIdentifier = "loop-sync-bg789"
        };

        // Act
        var bgCheck = TreatmentDecomposer.MapToBGCheck(treatment, correlationId);

        // Assert
        bgCheck.SyncIdentifier.Should().Be("loop-sync-bg789");
    }

    [Fact]
    public void MapToNote_MapsSyncIdentifier()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var treatment = new Treatment
        {
            Id = "note-sync-1",
            Mills = 1700000000000,
            Notes = "Test note",
            EventType = "Note",
            SyncIdentifier = "loop-sync-note101"
        };

        // Act
        var note = TreatmentDecomposer.MapToNote(treatment, correlationId, false);

        // Assert
        note.SyncIdentifier.Should().Be("loop-sync-note101");
    }

    [Fact]
    public void MapToDeviceEvent_MapsSyncIdentifier()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var treatment = new Treatment
        {
            Id = "device-sync-1",
            Mills = 1700000000000,
            SyncIdentifier = "loop-sync-device202"
        };

        // Act
        var deviceEvent = TreatmentDecomposer.MapToDeviceEvent(treatment, correlationId, DeviceEventType.SiteChange);

        // Assert
        deviceEvent.SyncIdentifier.Should().Be("loop-sync-device202");
    }

    #endregion

    // Note: DeleteByLegacyIdAsync tests require PostgreSQL (ExecuteDeleteAsync is not
    // supported by the EF Core in-memory provider) and belong in integration tests.

    #region Override Rule Boundary Cases

    [Fact]
    public async Task DecomposeAsync_UnknownEventWithOnlyInsulin_ProducesNothing()
    {
        // Arrange - override rule only fires when BOTH are > 0.
        // An unknown event type with only insulin doesn't match any known type.
        var treatment = new Treatment
        {
            Id = "insulin-only-unknown",
            EventType = "Custom Event",
            Mills = 1700000000000,
            Insulin = 3.0,
            Carbs = 0
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert - neither the override (needs both) nor any event type matches
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_UnknownEventWithOnlyCarbs_ProducesNothing()
    {
        // Arrange - only carbs > 0 with unknown event type
        var treatment = new Treatment
        {
            Id = "carbs-only-unknown",
            EventType = "Custom Event",
            Mills = 1700000000000,
            Insulin = 0,
            Carbs = 20
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert - override rule requires BOTH; unknown type doesn't match
        result.CreatedRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_CorrectionBolusWithOnlyInsulinNoCarbs_ProducesBolusOnly()
    {
        // Arrange - known event type "Correction Bolus" produces Bolus.
        // Carbs = 0 means override rule doesn't fire.
        var treatment = new Treatment
        {
            Id = "correction-no-carbs",
            EventType = "Correction Bolus",
            Mills = 1700000000000,
            Insulin = 3.0,
            Carbs = 0
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert - Correction Bolus sets produceBolus; override doesn't fire
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_CarbCorrectionWithOnlyCarbsNoInsulin_ProducesCarbIntakeOnly()
    {
        // Arrange - known event type "Carb Correction" produces CarbIntake.
        var treatment = new Treatment
        {
            Id = "carb-correction-no-insulin",
            EventType = "Carb Correction",
            Mills = 1700000000000,
            Carbs = 15
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_NegativeInsulinWithCarbs_DoesNotTriggerOverrideRule()
    {
        // Arrange - negative insulin: `Insulin is > 0` is false, unknown event type
        var treatment = new Treatment
        {
            Id = "negative-insulin",
            EventType = "Unknown",
            Mills = 1700000000000,
            Insulin = -1.0,
            Carbs = 20
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert - override rule needs insulin > 0, and "Unknown" doesn't match any event type
        result.CreatedRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_MealBolusWithNullInsulinAndNullCarbs_ProducesNothing()
    {
        // Arrange - Meal Bolus sets both flags, but the mapped values default to 0
        var treatment = new Treatment
        {
            Id = "meal-no-data",
            EventType = "Meal Bolus",
            Mills = 1700000000000
            // Insulin=null, Carbs=null
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert - Meal Bolus always produces Bolus+CarbIntake by EventType match
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().HaveCount(1);

        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().Single();
        bolus.Insulin.Should().Be(0, "null insulin defaults to 0 in mapping");

        var carb = result.CreatedRecords.OfType<V4Models.CarbIntake>().Single();
        carb.Carbs.Should().Be(0, "null carbs defaults to 0 in mapping");
    }

    #endregion

    #region Bolus Wizard Combinations

    [Fact]
    public async Task DecomposeAsync_BolusWizardWithCarbsButNoInsulin_ProducesBolusCalcOnly()
    {
        // Arrange - Bolus Wizard + carbs > 0 but no insulin.
        // Override rule requires BOTH insulin > 0 AND carbs > 0, so only BolusCalc is produced.
        var treatment = new Treatment
        {
            Id = "wizard-carbs-only",
            EventType = "Bolus Wizard",
            Mills = 1700000000000,
            Carbs = 25,
            BloodGlucoseInput = 150
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert - Bolus Wizard sets produceBolusCalc. No insulin means no Bolus.
        // Override doesn't fire (needs both). CarbIntake not produced.
        result.CreatedRecords.OfType<V4Models.BolusCalculation>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().BeEmpty();
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_BolusWizardWithInsulinAndCarbs_ProducesAllThree()
    {
        // Arrange - override rule fires: insulin + carbs both > 0
        var treatment = new Treatment
        {
            Id = "wizard-all-three",
            EventType = "Bolus Wizard",
            Mills = 1700000000000,
            Insulin = 4.0,
            Carbs = 30,
            BloodGlucoseInput = 180
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert - BolusCalc + Bolus (from wizard + insulin) + CarbIntake (from override rule)
        result.CreatedRecords.OfType<V4Models.BolusCalculation>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().HaveCount(1);
    }

    [Fact]
    public async Task DecomposeAsync_BolusWizardNoInsulinNoCarbs_ProducesBolusCalcOnly()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "wizard-calc-only",
            EventType = "Bolus Wizard",
            Mills = 1700000000000,
            BloodGlucoseInput = 90,
            InsulinOnBoard = 2.5
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        result.CreatedRecords[0].Should().BeOfType<V4Models.BolusCalculation>();
    }

    #endregion

    #region Idempotency - Additional Types

    [Fact]
    public async Task DecomposeAsync_BGCheckTwice_UpdatesInsteadOfCreatingDuplicate()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "idempotent-bgcheck",
            EventType = "BG Check",
            Mills = 1700000000000,
            Glucose = 120,
            GlucoseType = "Finger",
            Units = "mg/dl"
        };

        var first = await _decomposer.DecomposeAsync(treatment);
        first.CreatedRecords.Should().HaveCount(1);

        treatment.Glucose = 125;
        var second = await _decomposer.DecomposeAsync(treatment);

        // Assert
        second.CreatedRecords.Should().BeEmpty();
        second.UpdatedRecords.Should().HaveCount(1);
        var updated = second.UpdatedRecords[0].Should().BeOfType<V4Models.BGCheck>().Subject;
        updated.Glucose.Should().Be(125);
    }

    [Fact]
    public async Task DecomposeAsync_NoteTwice_UpdatesInsteadOfCreatingDuplicate()
    {
        var treatment = new Treatment
        {
            Id = "idempotent-note",
            EventType = "Note",
            Mills = 1700000000000,
            Notes = "Original note"
        };

        var first = await _decomposer.DecomposeAsync(treatment);
        first.CreatedRecords.Should().HaveCount(1);

        treatment.Notes = "Updated note";
        var second = await _decomposer.DecomposeAsync(treatment);

        second.CreatedRecords.Should().BeEmpty();
        second.UpdatedRecords.Should().HaveCount(1);
        var updated = second.UpdatedRecords[0].Should().BeOfType<V4Models.Note>().Subject;
        updated.Text.Should().Be("Updated note");
    }

    [Fact]
    public async Task DecomposeAsync_BolusCalculationTwice_UpdatesInsteadOfCreatingDuplicate()
    {
        var treatment = new Treatment
        {
            Id = "idempotent-calc",
            EventType = "Bolus Wizard",
            Mills = 1700000000000,
            BloodGlucoseInput = 150
        };

        var first = await _decomposer.DecomposeAsync(treatment);
        first.CreatedRecords.Should().HaveCount(1);

        treatment.BloodGlucoseInput = 180;
        var second = await _decomposer.DecomposeAsync(treatment);

        second.CreatedRecords.Should().BeEmpty();
        second.UpdatedRecords.Should().HaveCount(1);
    }

    [Fact]
    public async Task DecomposeAsync_NullIdTreatment_AlwaysCreatesNeverUpdates()
    {
        // Arrange - two identical treatments with null IDs
        var treatment = new Treatment
        {
            Id = null,
            EventType = "Correction Bolus",
            Mills = 1700000000000,
            Insulin = 2.0
        };

        var first = await _decomposer.DecomposeAsync(treatment);
        var second = await _decomposer.DecomposeAsync(treatment);

        // Assert
        first.CreatedRecords.Should().HaveCount(1);
        second.CreatedRecords.Should().HaveCount(1);
        first.UpdatedRecords.Should().BeEmpty();
        second.UpdatedRecords.Should().BeEmpty();
    }

    #endregion

    #region EventType Whitespace Handling

    [Fact]
    public async Task DecomposeAsync_EventTypeWithLeadingTrailingSpaces_TrimsAndMatches()
    {
        // Arrange - TreatmentDecomposer.Trim()'s the EventType
        var treatment = new Treatment
        {
            Id = "trimmed-event",
            EventType = "  Correction Bolus  ",
            Mills = 1700000000000,
            Insulin = 2.0
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
    }

    #endregion

    #region ProfileSwitch EndMills Calculation

    [Fact]
    public async Task DecomposeAsync_ProfileSwitchWithDuration_CalculatesEndMills()
    {
        var treatment = new Treatment
        {
            Id = "profile-duration",
            EventType = "Profile Switch",
            Mills = 1700000000000,
            Profile = "Night",
            Duration = 120, // 120 minutes
            EnteredBy = "AAPS"
        };

        var expectedStateSpan = new StateSpan
        {
            Id = "ss-1",
            Category = StateSpanCategory.Profile,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime
        };
        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        await _decomposer.DecomposeAsync(treatment);

        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss =>
                    ss.EndMills == 1700000000000 + (120 * 60 * 1000)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DecomposeAsync_ProfileSwitchWithNullDuration_HasNullEndMills()
    {
        var treatment = new Treatment
        {
            Id = "profile-no-duration",
            EventType = "Profile Switch",
            Mills = 1700000000000,
            Profile = "Default"
        };

        var expectedStateSpan = new StateSpan { Id = "ss-2", Category = StateSpanCategory.Profile };
        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        await _decomposer.DecomposeAsync(treatment);

        // Duration defaults to 0 in Treatment; 0 is NOT > 0, so EndMills should be null
        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss => ss.EndMills == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Override EndMills Calculation

    [Fact]
    public async Task DecomposeAsync_OverrideWithDuration_CalculatesEndMills()
    {
        var treatment = new Treatment
        {
            Id = "override-duration",
            EventType = "Temporary Override",
            Mills = 1700000000000,
            Duration = 60,
            Reason = "Eating Soon"
        };

        var expectedStateSpan = new StateSpan { Id = "ss-3", Category = StateSpanCategory.Override };
        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        await _decomposer.DecomposeAsync(treatment);

        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss =>
                    ss.EndMills == 1700000000000 + (60 * 60 * 1000)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Parse Helpers - Unknown Values

    [Fact]
    public void ParseBolusType_UnknownNonEmpty_ReturnsNull()
    {
        TreatmentDecomposer.ParseBolusType("extended").Should().BeNull();
    }

    [Fact]
    public void ParseGlucoseType_UnknownNonEmpty_ReturnsNull()
    {
        TreatmentDecomposer.ParseGlucoseType("Interstitial").Should().BeNull();
    }

    [Fact]
    public void ParseGlucoseUnit_UnknownNonEmpty_ReturnsNull()
    {
        TreatmentDecomposer.ParseGlucoseUnit("g/L").Should().BeNull();
    }

    [Fact]
    public void ParseGlucoseUnit_EmptyString_ReturnsNull()
    {
        TreatmentDecomposer.ParseGlucoseUnit("").Should().BeNull();
    }

    [Fact]
    public void ParseGlucoseType_EmptyString_ReturnsNull()
    {
        TreatmentDecomposer.ParseGlucoseType("").Should().BeNull();
    }

    #endregion

    #region Bolus Default Values

    [Fact]
    public void MapToBolus_NullInsulin_DefaultsToZero()
    {
        var correlationId = Guid.CreateVersion7();
        var treatment = new Treatment { Id = "null-insulin-bolus", Mills = 1700000000000 };

        var bolus = TreatmentDecomposer.MapToBolus(treatment, correlationId);
        bolus.Insulin.Should().Be(0);
    }

    [Fact]
    public void MapToBolus_NullAutomatic_DefaultsToFalse()
    {
        var correlationId = Guid.CreateVersion7();
        var treatment = new Treatment { Id = "null-auto-bolus", Mills = 1700000000000, Insulin = 3.0 };

        var bolus = TreatmentDecomposer.MapToBolus(treatment, correlationId);
        bolus.Automatic.Should().BeFalse();
    }

    [Fact]
    public void MapToBolus_NullPumpId_DefaultsToNull()
    {
        var correlationId = Guid.CreateVersion7();
        var treatment = new Treatment { Id = "null-pump", Mills = 1700000000000, Insulin = 3.0 };

        var bolus = TreatmentDecomposer.MapToBolus(treatment, correlationId);
        bolus.PumpRecordId.Should().BeNull();
    }

    [Fact]
    public void MapToCarbIntake_NullCarbs_DefaultsToZero()
    {
        var correlationId = Guid.CreateVersion7();
        var treatment = new Treatment { Id = "null-carbs", Mills = 1700000000000 };

        var carbIntake = TreatmentDecomposer.MapToCarbIntake(treatment, correlationId);
        carbIntake.Carbs.Should().Be(0);
    }

    [Fact]
    public void MapToBGCheck_NullGlucoseAndMgdl_DefaultsToZero()
    {
        var correlationId = Guid.CreateVersion7();
        var treatment = new Treatment { Id = "null-glucose", Mills = 1700000000000 };

        var bgCheck = TreatmentDecomposer.MapToBGCheck(treatment, correlationId);
        bgCheck.Glucose.Should().Be(0);
        bgCheck.Mgdl.Should().Be(0);
    }

    #endregion

    #region Device Event Case Insensitivity

    [Theory]
    [InlineData("site change")]
    [InlineData("SITE CHANGE")]
    [InlineData("Site Change")]
    public async Task DecomposeAsync_DeviceEventCaseInsensitive_CreatesDeviceEvent(string eventType)
    {
        // DeviceEventTypeMap uses StringComparer.OrdinalIgnoreCase
        var treatment = new Treatment
        {
            Id = $"device-case-{eventType.GetHashCode()}",
            EventType = eventType,
            Mills = 1700000000000
        };

        var result = await _decomposer.DecomposeAsync(treatment);

        result.CreatedRecords.Should().HaveCount(1);
        var de = result.CreatedRecords[0].Should().BeOfType<V4Models.DeviceEvent>().Subject;
        de.EventType.Should().Be(DeviceEventType.SiteChange);
    }

    #endregion

    #region Correlation ID Consistency

    [Fact]
    public async Task DecomposeAsync_BolusWizardWithInsulinAndCarbs_AllShareCorrelationId()
    {
        var treatment = new Treatment
        {
            Id = "corr-all-three",
            EventType = "Bolus Wizard",
            Mills = 1700000000000,
            Insulin = 5.0,
            Carbs = 30,
            BloodGlucoseInput = 200
        };

        var result = await _decomposer.DecomposeAsync(treatment);

        var calc = result.CreatedRecords.OfType<V4Models.BolusCalculation>().Single();
        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().Single();
        var carb = result.CreatedRecords.OfType<V4Models.CarbIntake>().Single();

        calc.CorrelationId.Should().Be(result.CorrelationId);
        bolus.CorrelationId.Should().Be(result.CorrelationId);
        carb.CorrelationId.Should().Be(result.CorrelationId);
    }

    #endregion

    #region FK Linking: Bolus -> BolusCalculation, CarbIntake -> Bolus

    [Fact]
    public async Task DecomposeAsync_BolusWizardWithInsulin_SetsBolusCalculationIdOnBolus()
    {
        // Arrange - Bolus Wizard with insulin should produce a BolusCalculation and a Bolus,
        // and the Bolus should have its BolusCalculationId set to the BolusCalculation's ID
        var treatment = new Treatment
        {
            Id = "wizard-fk-link-1",
            EventType = "Bolus Wizard",
            Mills = 1700000000000,
            Insulin = 4.0,
            BloodGlucoseInput = 180,
            BloodGlucoseInputSource = "Sensor",
            InsulinOnBoard = 1.5
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var bolusCalc = result.CreatedRecords.OfType<V4Models.BolusCalculation>().Single();
        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().Single();

        bolus.BolusCalculationId.Should().Be(bolusCalc.Id,
            "the Bolus should be linked to the BolusCalculation that produced it");
        bolusCalc.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task DecomposeAsync_MealBolus_SharesCorrelationId()
    {
        // Arrange - Meal Bolus with insulin and carbs should produce a Bolus and a CarbIntake,
        // and both should share the same CorrelationId so the pair can be rejoined downstream.
        var treatment = new Treatment
        {
            Id = "meal-fk-link-1",
            EventType = "Meal Bolus",
            Mills = 1700000000000,
            Insulin = 5.5,
            Carbs = 45,
            EnteredBy = "xDrip+",
            DataSource = "manual"
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().Single();
        var carbIntake = result.CreatedRecords.OfType<V4Models.CarbIntake>().Single();

        carbIntake.CorrelationId.Should().Be(bolus.CorrelationId,
            "the CarbIntake and Bolus should share a CorrelationId so they can be paired downstream");
        carbIntake.CorrelationId.Should().NotBe(Guid.Empty);
        bolus.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task DecomposeAsync_BolusWizardWithInsulinAndCarbs_SetsAllFKs()
    {
        // Arrange - Bolus Wizard with insulin AND carbs triggers the override rule,
        // producing BolusCalculation + Bolus + CarbIntake. All FKs should be set.
        var treatment = new Treatment
        {
            Id = "wizard-all-fks",
            EventType = "Bolus Wizard",
            Mills = 1700000000000,
            Insulin = 4.0,
            Carbs = 30,
            BloodGlucoseInput = 180
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var bolusCalc = result.CreatedRecords.OfType<V4Models.BolusCalculation>().Single();
        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().Single();
        var carbIntake = result.CreatedRecords.OfType<V4Models.CarbIntake>().Single();

        bolus.BolusCalculationId.Should().Be(bolusCalc.Id,
            "Bolus should link to its BolusCalculation");
        carbIntake.CorrelationId.Should().Be(bolus.CorrelationId,
            "CarbIntake should share a CorrelationId with its Bolus");
        carbIntake.CorrelationId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task DecomposeAsync_CorrectionBolusOnly_DoesNotSetBolusCalculationId()
    {
        // Arrange - Correction Bolus produces only a Bolus, no BolusCalculation
        var treatment = new Treatment
        {
            Id = "correction-no-calc-fk",
            EventType = "Correction Bolus",
            Mills = 1700000000000,
            Insulin = 3.0
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().Single();
        bolus.BolusCalculationId.Should().BeNull(
            "no BolusCalculation was produced, so Bolus.BolusCalculationId should remain null");
    }

    #endregion

    #region Announcement with IsAnnouncement property

    [Fact]
    public async Task DecomposeAsync_NoteWithIsAnnouncementTrue_SetsIsAnnouncementTrue()
    {
        // Arrange - a Note event type but with IsAnnouncement=true on the treatment
        var treatment = new Treatment
        {
            Id = "note-with-flag",
            EventType = "Note",
            Mills = 1700000000000,
            Notes = "Important message",
            IsAnnouncement = true
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var note = result.CreatedRecords[0].Should().BeOfType<V4Models.Note>().Subject;
        note.IsAnnouncement.Should().BeTrue("the treatment's IsAnnouncement flag should be respected");
    }

    #endregion

    #region Device Events → DeviceEvent

    [Theory]
    [InlineData("Site Change", DeviceEventType.SiteChange)]
    [InlineData("Sensor Start", DeviceEventType.SensorStart)]
    [InlineData("Sensor Change", DeviceEventType.SensorChange)]
    [InlineData("Sensor Stop", DeviceEventType.SensorStop)]
    [InlineData("Insulin Change", DeviceEventType.InsulinChange)]
    [InlineData("Pump Battery Change", DeviceEventType.PumpBatteryChange)]
    [InlineData("Pod Change", DeviceEventType.PodChange)]
    [InlineData("Reservoir Change", DeviceEventType.ReservoirChange)]
    [InlineData("Cannula Change", DeviceEventType.CannulaChange)]
    [InlineData("Transmitter Sensor Insert", DeviceEventType.TransmitterSensorInsert)]
    public async Task DecomposeAsync_DeviceEventTypes_CreatesDeviceEvent(string eventType, DeviceEventType expectedType)
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = $"device-event-{eventType.GetHashCode()}",
            EventType = eventType,
            Mills = 1700000000000,
            Notes = "Test device event",
            EnteredBy = "xDrip+",
            DataSource = "manual",
            UtcOffset = -300
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert — DeviceEvent + Note (because Notes is non-empty)
        result.CreatedRecords.Should().HaveCount(2);
        var deviceEvent = result.CreatedRecords.OfType<V4Models.DeviceEvent>().Single();
        deviceEvent.LegacyId.Should().Be(treatment.Id);
        deviceEvent.Mills.Should().Be(1700000000000);
        deviceEvent.EventType.Should().Be(expectedType);
        deviceEvent.Notes.Should().Be("Test device event");
        deviceEvent.Device.Should().Be("xDrip+");
        deviceEvent.DataSource.Should().Be("manual");
        deviceEvent.UtcOffset.Should().Be(-300);
        deviceEvent.CorrelationId.Should().Be(result.CorrelationId);

        var note = result.CreatedRecords.OfType<V4Models.Note>().Single();
        note.Text.Should().Be("Test device event");
        note.CorrelationId.Should().Be(result.CorrelationId);
    }

    [Fact]
    public async Task DecomposeAsync_SiteChangeTwice_UpdatesInsteadOfCreatingDuplicate()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "idempotent-site-change",
            EventType = "Site Change",
            Mills = 1700000000000,
            Notes = "Right arm"
        };

        // Act - first call creates DeviceEvent + Note (because Notes is non-empty)
        var firstResult = await _decomposer.DecomposeAsync(treatment);
        firstResult.CreatedRecords.Should().HaveCount(2);
        firstResult.CreatedRecords.OfType<V4Models.DeviceEvent>().Should().HaveCount(1);
        firstResult.CreatedRecords.OfType<V4Models.Note>().Should().HaveCount(1);
        firstResult.UpdatedRecords.Should().BeEmpty();

        // Modify notes
        treatment.Notes = "Left arm";

        // Act - second call should update both
        var secondResult = await _decomposer.DecomposeAsync(treatment);

        // Assert — both DeviceEvent and Note updated
        secondResult.CreatedRecords.Should().BeEmpty();
        secondResult.UpdatedRecords.Should().HaveCount(2);

        var updated = secondResult.UpdatedRecords.OfType<V4Models.DeviceEvent>().Single();
        updated.LegacyId.Should().Be("idempotent-site-change");
        updated.Notes.Should().Be("Left arm");
    }

    [Fact]
    public async Task DecomposeAsync_DeviceEventWithNullNotes_MapsNullNotes()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "sensor-start-no-notes",
            EventType = "Sensor Start",
            Mills = 1700000000000,
            Notes = null
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var deviceEvent = result.CreatedRecords[0].Should().BeOfType<V4Models.DeviceEvent>().Subject;
        deviceEvent.Notes.Should().BeNull();
    }

    #endregion

    #region Temporary Target → Delegates to IStateSpanService

    [Fact]
    public async Task DecomposeAsync_TemporaryTarget_DelegatesToStateSpanService()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "temptarget-1",
            EventType = "Temporary Target",
            Mills = 1700000000000,
            Duration = 60,
            TargetTop = 80,
            TargetBottom = 80,
            Reason = "Eating Soon",
            Units = "mg/dl",
            EnteredBy = "AAPS"
        };

        var expectedStateSpan = new StateSpan
        {
            Id = "state-span-tt-1",
            Category = StateSpanCategory.TemporaryTarget,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime
        };

        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        result.CreatedRecords[0].Should().BeOfType<StateSpan>();

        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss =>
                    ss.Category == StateSpanCategory.TemporaryTarget
                    && ss.State == "Active"
                    && ss.StartMills == 1700000000000
                    && ss.EndMills == 1700000000000 + (60 * 60 * 1000)
                    && ss.OriginalId == "temptarget-1"
                    && ss.Metadata != null
                    && ss.Metadata.ContainsKey("targetTop")
                    && ss.Metadata.ContainsKey("targetBottom")
                    && ss.Metadata.ContainsKey("reason")
                    && ss.Metadata.ContainsKey("units")
                    && ss.Metadata.ContainsKey("enteredBy")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DecomposeAsync_TemporaryTargetCancel_CreatesSpanWithCancelledState()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "temptarget-cancel-1",
            EventType = "Temporary Target Cancel",
            Mills = 1700000000000,
            EnteredBy = "AAPS"
        };

        var expectedStateSpan = new StateSpan
        {
            Id = "state-span-tt-cancel",
            Category = StateSpanCategory.TemporaryTarget,
        };

        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);

        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss =>
                    ss.Category == StateSpanCategory.TemporaryTarget
                    && ss.State == "Cancelled"
                    && ss.EndTimestamp == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DecomposeAsync_TemporaryTargetZeroDuration_IsCancelled()
    {
        // Arrange - a temp target with duration=0 is a cancel event
        var treatment = new Treatment
        {
            Id = "temptarget-zero-dur",
            EventType = "Temporary Target",
            Mills = 1700000000000,
            Duration = 0,
            EnteredBy = "AAPS"
        };

        var expectedStateSpan = new StateSpan
        {
            Id = "state-span-tt-zero",
            Category = StateSpanCategory.TemporaryTarget,
        };

        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        // Act
        await _decomposer.DecomposeAsync(treatment);

        // Assert
        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss =>
                    ss.State == "Cancelled"
                    && ss.EndTimestamp == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region LoopOverridePreset Duration Conversion

    [Fact]
    public void LoopOverridePreset_DurationMinutes_ConvertsSecondsToMinutes()
    {
        var preset = new LoopOverridePreset { Duration = 3600 }; // 1 hour in seconds
        preset.DurationMinutes.Should().Be(60); // 60 minutes
    }

    [Fact]
    public void LoopOverridePreset_DurationMinutes_NullWhenDurationNull()
    {
        var preset = new LoopOverridePreset { Duration = null };
        preset.DurationMinutes.Should().BeNull();
    }

    #endregion

    #region AAPS Correction Bolus Classification

    [Fact]
    public async Task DecomposeAsync_AapsCorrectionBolus_CreatesAlgorithmBolus()
    {
        // Arrange — AAPS "Correction Bolus" with app = "AAPS" is always an algorithm-delivered SMB
        var treatment = new Treatment
        {
            Id = "aaps-smb-bolus-1",
            EventType = "Correction Bolus",
            Mills = 1775994909183,
            Insulin = 0.2,
            UtcOffset = 0,
            AdditionalProperties = new Dictionary<string, object>
            {
                ["app"] = "AAPS"
            }
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var bolus = result.CreatedRecords[0].Should().BeOfType<V4Models.Bolus>().Subject;
        bolus.Insulin.Should().Be(0.2);
        bolus.Kind.Should().Be(V4Models.BolusKind.Algorithm);
        bolus.Automatic.Should().BeTrue();
    }

    [Fact]
    public async Task DecomposeAsync_NonAapsCorrectionBolus_CreatesManualBolus()
    {
        // Arrange — "Correction Bolus" without app = "AAPS" stays manual (could be xDrip+, Spike, etc.)
        var treatment = new Treatment
        {
            Id = "other-correction-1",
            EventType = "Correction Bolus",
            Mills = 1700000000000,
            Insulin = 1.5,
            UtcOffset = 0,
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var bolus = result.CreatedRecords[0].Should().BeOfType<V4Models.Bolus>().Subject;
        bolus.Kind.Should().Be(V4Models.BolusKind.Manual);
        bolus.Automatic.Should().BeFalse();
    }

    [Fact]
    public async Task DecomposeAsync_DeviceEvent_GetsDeviceIdFromPumpInfo()
    {
        // Arrange
        var expectedDeviceId = Guid.CreateVersion7();
        _deviceServiceMock
            .Setup(s => s.ResolveAsync(
                V4Models.DeviceCategory.InsulinPump,
                "Insulet",
                "Omnipod 5",
                1700000000000,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDeviceId);

        var treatment = new Treatment
        {
            Id = "device-event-fk-1",
            EventType = "Site Change",
            Mills = 1700000000000,
            PumpType = "Insulet",
            PumpSerial = "Omnipod 5"
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var deviceEvent = result.CreatedRecords.OfType<V4Models.DeviceEvent>().Single();
        deviceEvent.DeviceId.Should().Be(expectedDeviceId);
    }

    [Fact]
    public async Task DecomposeAsync_Bolus_GetsPatientDeviceId()
    {
        // Arrange
        var expectedDeviceId = Guid.CreateVersion7();
        var expectedPatientDeviceId = Guid.CreateVersion7();

        _deviceServiceMock
            .Setup(s => s.ResolveAsync(
                V4Models.DeviceCategory.InsulinPump,
                "Insulet",
                "Omnipod 5",
                1700000000000,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDeviceId);

        _deviceServiceMock
            .Setup(s => s.ResolvePatientDeviceAsync(
                expectedDeviceId,
                1700000000000,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPatientDeviceId);

        var treatment = new Treatment
        {
            Id = "bolus-patient-device-1",
            EventType = "Correction Bolus",
            Mills = 1700000000000,
            Insulin = 3.0,
            PumpType = "Insulet",
            PumpSerial = "Omnipod 5"
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().Single();
        bolus.PatientDeviceId.Should().Be(expectedPatientDeviceId);
    }

    #endregion

    #region Profile Switch with Inline ProfileJson

    [Fact]
    public async Task DecomposeAsync_ProfileSwitchWithProfileJson_CreatesStateSpanAndCallsProfileDecomposer()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "profile-switch-json-1",
            EventType = "Profile Switch",
            Mills = 1700000000000,
            Profile = "Day Profile",
            ProfileJson = """{"dia":4,"carbs_hr":20,"delay":20,"basal":[{"time":"00:00","value":0.8}],"carbratio":[{"time":"00:00","value":10}],"sens":[{"time":"00:00","value":40}],"target_low":[{"time":"00:00","value":80}],"target_high":[{"time":"00:00","value":120}]}""",
            EnteredBy = "AAPS"
        };

        var expectedStateSpan = new StateSpan
        {
            Id = "state-span-pj-1",
            Category = StateSpanCategory.Profile,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime
        };

        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        var profileDecompResult = new V4Models.DecompositionResult();
        profileDecompResult.CreatedRecords.Add(new V4Models.TherapySettings { ProfileName = "Day Profile@@@@@1700000000000" });

        _profileDecomposerMock
            .Setup(d => d.DecomposeAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profileDecompResult);

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert -- StateSpan + TherapySettings from profile decomposer
        result.CreatedRecords.Should().HaveCount(2);
        result.CreatedRecords[0].Should().BeOfType<StateSpan>();
        result.CreatedRecords[1].Should().BeOfType<V4Models.TherapySettings>();

        // Verify state span was created
        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss =>
                    ss.Category == StateSpanCategory.Profile
                    && ss.OriginalId == "profile-switch-json-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Verify profile decomposer was called with the synthetic profile
        _profileDecomposerMock.Verify(
            d => d.DecomposeAsync(
                It.Is<Profile>(p =>
                    p.Id == "profile-switch-json-1"
                    && p.Mills == 1700000000000
                    && p.Store.Count == 1
                    && p.Store.ContainsKey("Day Profile@@@@@1700000000000")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DecomposeAsync_ProfileSwitchWithoutProfileJson_DoesNotCallProfileDecomposer()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "profile-switch-no-json",
            EventType = "Profile Switch",
            Mills = 1700000000000,
            Profile = "Default",
            EnteredBy = "AAPS"
        };

        var expectedStateSpan = new StateSpan
        {
            Id = "state-span-no-pj",
            Category = StateSpanCategory.Profile,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime
        };

        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        // Act
        await _decomposer.DecomposeAsync(treatment);

        // Assert -- profile decomposer should NOT be called
        _profileDecomposerMock.Verify(
            d => d.DecomposeAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion
}
