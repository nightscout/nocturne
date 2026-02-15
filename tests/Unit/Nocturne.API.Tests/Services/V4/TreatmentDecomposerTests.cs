using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts;
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
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        var bolusRepo = new BolusRepository(_context, NullLogger<BolusRepository>.Instance);
        var carbIntakeRepo = new CarbIntakeRepository(_context, NullLogger<CarbIntakeRepository>.Instance);
        var bgCheckRepo = new BGCheckRepository(_context, NullLogger<BGCheckRepository>.Instance);
        var noteRepo = new NoteRepository(_context, NullLogger<NoteRepository>.Instance);
        var deviceEventRepo = new DeviceEventRepository(_context, NullLogger<DeviceEventRepository>.Instance);
        var bolusCalcRepo = new BolusCalculationRepository(_context, NullLogger<BolusCalculationRepository>.Instance);
        _stateSpanServiceMock = new Mock<IStateSpanService>();

        _decomposer = new TreatmentDecomposer(
            bolusRepo, carbIntakeRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
            _stateSpanServiceMock.Object,
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
        carbIntake.Protein.Should().Be(10);
        carbIntake.Fat.Should().Be(5);
        carbIntake.FoodType.Should().Be("Sandwich");
        carbIntake.AbsorptionTime.Should().Be(120);
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
        carbIntake.FoodType.Should().Be("Juice");
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
        bgCheck.Mgdl.Should().Be(120);
        bgCheck.Mmol.Should().Be(6.7);
        bgCheck.Units.Should().Be(V4Models.GlucoseUnit.MgDl);
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

    #region TempBasal → Delegates to IStateSpanService

    [Theory]
    [InlineData("Temp Basal")]
    [InlineData("Temp Basal Start")]
    [InlineData("TempBasal")]
    public async Task DecomposeAsync_TempBasal_DelegatesToStateSpanService(string eventType)
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

        var expectedStateSpan = new StateSpan
        {
            Id = "state-span-123",
            Category = StateSpanCategory.BasalDelivery,
            StartMills = 1700000000000
        };

        _stateSpanServiceMock
            .Setup(s => s.CreateBasalDeliveryFromTreatmentAsync(treatment, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        result.CreatedRecords[0].Should().BeOfType<StateSpan>();
        _stateSpanServiceMock.Verify(
            s => s.CreateBasalDeliveryFromTreatmentAsync(treatment, It.IsAny<CancellationToken>()),
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
            StartMills = 1700000000000
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
    public async Task DecomposeAsync_BGCheckWithGlucoseButNoMgdl_UseGlucoseForMgdl()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "bgcheck-fallback",
            EventType = "BG Check",
            Mills = 1700000000000,
            Glucose = 130,
            // Mgdl is null
            GlucoseType = "Sensor",
            Units = "mmol/l"
        };

        // Act
        var result = await _decomposer.DecomposeAsync(treatment);

        // Assert
        var bgCheck = result.CreatedRecords[0].Should().BeOfType<V4Models.BGCheck>().Subject;
        bgCheck.Glucose.Should().Be(130);
        bgCheck.Mgdl.Should().Be(130, "should fall back to Glucose when Mgdl is null");
        bgCheck.GlucoseType.Should().Be(V4Models.GlucoseType.Sensor);
        bgCheck.Units.Should().Be(V4Models.GlucoseUnit.Mmol);
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
            StartMills = 1700000000000
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
        bolus.IsBasalInsulin.Should().BeTrue();
        bolus.PumpId.Should().Be("42");
        bolus.PumpSerial.Should().Be("SN-12345");
        bolus.PumpType.Should().Be("Omnipod DASH");
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

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var deviceEvent = result.CreatedRecords[0].Should().BeOfType<V4Models.DeviceEvent>().Subject;
        deviceEvent.LegacyId.Should().Be(treatment.Id);
        deviceEvent.Mills.Should().Be(1700000000000);
        deviceEvent.EventType.Should().Be(expectedType);
        deviceEvent.Notes.Should().Be("Test device event");
        deviceEvent.Device.Should().Be("xDrip+");
        deviceEvent.DataSource.Should().Be("manual");
        deviceEvent.UtcOffset.Should().Be(-300);
        deviceEvent.CorrelationId.Should().Be(result.CorrelationId);
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

        // Act - first call creates
        var firstResult = await _decomposer.DecomposeAsync(treatment);
        firstResult.CreatedRecords.Should().HaveCount(1);
        firstResult.UpdatedRecords.Should().BeEmpty();

        // Modify notes
        treatment.Notes = "Left arm";

        // Act - second call should update
        var secondResult = await _decomposer.DecomposeAsync(treatment);

        // Assert
        secondResult.CreatedRecords.Should().BeEmpty();
        secondResult.UpdatedRecords.Should().HaveCount(1);

        var updated = secondResult.UpdatedRecords[0].Should().BeOfType<V4Models.DeviceEvent>().Subject;
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
}
