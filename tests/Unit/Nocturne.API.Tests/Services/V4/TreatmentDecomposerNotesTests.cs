using Nocturne.API.Tests.TestDoubles;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

public class TreatmentDecomposerNotesTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerNotesTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var stateSpanServiceMock = new Mock<IStateSpanService>().AnswersBatchFormsPerSpan();
        var treatmentFoodServiceMock = new Mock<ITreatmentFoodService>();
        var tempBasalRepoMock = new Mock<ITempBasalRepository>().AnswersBatchFormsPerRecord();
        var deviceServiceMock = new Mock<IDeviceService>().AnswersBatchFormsPerRecord();

        deviceServiceMock
            .Setup(s => s.ResolveAsync(It.IsAny<V4Models.DeviceCategory>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        _decomposer = TreatmentDecomposerDoubles.Stored(
            _context.AsFactory(), Mock.Of<IDuplicateLinker>(), Mock.Of<IAuditContext>(),
            tempBasal: tempBasalRepoMock.Object,
            stateSpans: stateSpanServiceMock.Object,
            foods: treatmentFoodServiceMock.Object,
            devices: deviceServiceMock.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DecomposeAsync_MealBolusWithNotes_ProducesBolusAndCarbIntakeAndNote()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "meal-bolus-notes-1",
            EventType = "Meal Bolus",
            Mills = 1700000000000,
            Insulin = 5.5,
            Carbs = 45,
            Notes = "birthday cake"
        };

        // Act
        var result = await _decomposer.DecomposeOneAsync(treatment, WriteOrigin.Live);

        // Assert — should produce Bolus + CarbIntake + Note
        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.Note>().Should().HaveCount(1);

        var note = result.CreatedRecords.OfType<V4Models.Note>().Single();
        note.Text.Should().Be("birthday cake");
        note.LegacyId.Should().Be("meal-bolus-notes-1");
        note.CorrelationId.Should().Be(result.CorrelationId);
    }

    [Fact]
    public async Task DecomposeAsync_CorrectionBolusWithNotes_ProducesBolusAndNote()
    {
        var treatment = new Treatment
        {
            Id = "correction-notes-1",
            EventType = "Correction Bolus",
            Mills = 1700000000000,
            Insulin = 2.0,
            Notes = "high after lunch"
        };

        var result = await _decomposer.DecomposeOneAsync(treatment, WriteOrigin.Live);

        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.Note>().Should().HaveCount(1);

        var note = result.CreatedRecords.OfType<V4Models.Note>().Single();
        note.Text.Should().Be("high after lunch");
    }

    [Fact]
    public async Task DecomposeAsync_BGCheckWithNotes_ProducesBGCheckAndNote()
    {
        var treatment = new Treatment
        {
            Id = "bgcheck-notes-1",
            EventType = "BG Check",
            Mills = 1700000000000,
            Glucose = 120,
            GlucoseType = "Finger",
            Notes = "before dinner"
        };

        var result = await _decomposer.DecomposeOneAsync(treatment, WriteOrigin.Live);

        result.CreatedRecords.OfType<V4Models.BGCheck>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.Note>().Should().HaveCount(1);

        var note = result.CreatedRecords.OfType<V4Models.Note>().Single();
        note.Text.Should().Be("before dinner");
    }

    [Fact]
    public async Task DecomposeAsync_NoteEventType_DoesNotProduceDuplicateNote()
    {
        // A treatment with EventType "Note" already produces a Note — should not double up
        var treatment = new Treatment
        {
            Id = "note-1",
            EventType = "Note",
            Mills = 1700000000000,
            Notes = "regular note text"
        };

        var result = await _decomposer.DecomposeOneAsync(treatment, WriteOrigin.Live);

        result.CreatedRecords.OfType<V4Models.Note>().Should().HaveCount(1);
    }

    [Fact]
    public async Task DecomposeAsync_MealBolusWithEmptyNotes_DoesNotProduceNote()
    {
        var treatment = new Treatment
        {
            Id = "meal-bolus-empty-1",
            EventType = "Meal Bolus",
            Mills = 1700000000000,
            Insulin = 5.0,
            Carbs = 30,
            Notes = ""
        };

        var result = await _decomposer.DecomposeOneAsync(treatment, WriteOrigin.Live);

        result.CreatedRecords.OfType<V4Models.Bolus>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.CarbIntake>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.Note>().Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_MealBolusWithWhitespaceNotes_DoesNotProduceNote()
    {
        var treatment = new Treatment
        {
            Id = "meal-bolus-ws-1",
            EventType = "Meal Bolus",
            Mills = 1700000000000,
            Insulin = 5.0,
            Carbs = 30,
            Notes = "   "
        };

        var result = await _decomposer.DecomposeOneAsync(treatment, WriteOrigin.Live);

        result.CreatedRecords.OfType<V4Models.Note>().Should().BeEmpty();
    }
}
