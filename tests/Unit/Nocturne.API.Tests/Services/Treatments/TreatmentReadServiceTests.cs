using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

public class TreatmentReadServiceTests
{
    private readonly Mock<IV4ToLegacyProjectionService> _projection = new();
    private readonly Mock<ITreatmentDecomposer> _decomposer = new();
    private readonly Mock<IDecompositionPipeline> _pipeline = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepo = new();
    private readonly Mock<IBolusRepository> _bolusRepo = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepo = new();
    private readonly Mock<IBGCheckRepository> _bgCheckRepo = new();
    private readonly Mock<INoteRepository> _noteRepo = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepo = new();
    private readonly Mock<IBolusCalculationRepository> _bolusCalcRepo = new();
    private readonly TreatmentReadService _service;

    public TreatmentReadServiceTests()
    {
        _service = new TreatmentReadService(
            _projection.Object,
            _decomposer.Object,
            _pipeline.Object,
            _tempBasalRepo.Object,
            _bolusRepo.Object,
            _carbIntakeRepo.Object,
            _bgCheckRepo.Object,
            _noteRepo.Object,
            _deviceEventRepo.Object,
            _bolusCalcRepo.Object,
            NullLogger<TreatmentReadService>.Instance);
    }

    [Fact]
    public async Task QueryAsync_DelegatesToProjectionWithNativeOnlyFalse()
    {
        var treatments = new List<Treatment>
        {
            new() { Id = "1", Mills = 1000 },
            new() { Id = "2", Mills = 2000 },
        };

        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(null, null, 10, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatments);

        var result = await _service.QueryAsync(new TreatmentQuery { Count = 10 });

        result.Should().HaveCount(2);
        _projection.Verify(
            p => p.GetProjectedTreatmentsAsync(null, null, 10, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task QueryAsync_AppliesSkipAndTake()
    {
        var treatments = new List<Treatment>
        {
            new() { Id = "1", Mills = 3000 },
            new() { Id = "2", Mills = 2000 },
            new() { Id = "3", Mills = 1000 },
        };

        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(null, null, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatments);

        var result = await _service.QueryAsync(new TreatmentQuery { Count = 1, Skip = 1 });

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("2");
    }

    [Fact]
    public async Task QueryAsync_ReverseResults_ReturnsAscendingOrder()
    {
        var treatments = new List<Treatment>
        {
            new() { Id = "1", Mills = 3000 },
            new() { Id = "2", Mills = 2000 },
            new() { Id = "3", Mills = 1000 },
        };

        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(null, null, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatments);

        var result = await _service.QueryAsync(new TreatmentQuery { Count = 10, ReverseResults = true });

        result[0].Mills.Should().Be(1000);
        result[2].Mills.Should().Be(3000);
    }

    [Fact]
    public async Task GetByIdAsync_WithGuid_SearchesV4Repos()
    {
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime;
        var bolus = new Bolus { Id = id, Timestamp = timestamp };
        var projected = new List<Treatment> { new() { Id = id.ToString(), Mills = 1000 } };

        _bolusRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(1000, 1000, 100, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(projected);

        var result = await _service.GetByIdAsync(id.ToString());

        result.Should().NotBeNull();
        result!.Id.Should().Be(id.ToString());
    }

    [Fact]
    public async Task GetByIdAsync_WithLegacyId_SearchesByLegacyId()
    {
        var legacyId = "abc123";
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime;
        var bolus = new Bolus { Id = Guid.NewGuid(), Timestamp = timestamp, LegacyId = legacyId };
        var projected = new List<Treatment> { new() { Id = bolus.Id.ToString(), Mills = 1000 } };

        _bolusRepo.Setup(r => r.GetByLegacyIdAsync(legacyId, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(1000, 1000, 100, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(projected);

        var result = await _service.GetByIdAsync(legacyId);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsNull()
    {
        _bolusRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Bolus?)null);
        _carbIntakeRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((CarbIntake?)null);
        _bgCheckRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((BGCheck?)null);
        _noteRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Note?)null);
        _deviceEventRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DeviceEvent?)null);
        _bolusCalcRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((BolusCalculation?)null);
        _tempBasalRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((TempBasal?)null);

        var result = await _service.GetByIdAsync(Guid.NewGuid().ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_DecomposesEachTreatment()
    {
        var treatment = new Treatment { Id = "t1", Mills = 1000, EventType = "Note", Notes = "test" };
        var decompositionResult = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        decompositionResult.CreatedRecords.Add(new Note
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime
        });

        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decompositionResult);

        var result = await _service.CreateAsync([treatment]);

        result.Should().HaveCount(1);
        _decomposer.Verify(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WhenDecompositionCanceled_PropagatesWithoutTouchingRemaining()
    {
        var canceled = new Treatment { Id = "t1", Mills = 1000, EventType = "Note" };
        var next = new Treatment { Id = "t2", Mills = 2000, EventType = "Note" };

        _decomposer
            .Setup(d => d.DecomposeAsync(canceled, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _service.CreateAsync([canceled, next]);

        // Cancellation must abort the batch, not be swallowed as a per-record failure.
        await act.Should().ThrowAsync<OperationCanceledException>();
        _decomposer.Verify(
            d => d.DecomposeAsync(next, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WhenDecompositionFailsForOneRecord_SkipsItAndContinues()
    {
        var bad = new Treatment { Id = "t1", Mills = 1000, EventType = "Note" };
        var good = new Treatment { Id = "t2", Mills = 2000, EventType = "Note" };

        _decomposer
            .Setup(d => d.DecomposeAsync(bad, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _decomposer
            .Setup(d => d.DecomposeAsync(good, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { CorrelationId = Guid.NewGuid() });

        var result = await _service.CreateAsync([bad, good]);

        // A genuine per-record failure is still isolated: the bad record is dropped,
        // the good one is kept.
        result.Should().ContainSingle().Which.Id.Should().Be("t2");
    }

    private async Task<BulkWrite<Treatment>> CreateWithAsync(Treatment treatment, DecompositionResult result)
    {
        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return await _service.CreateAsync([treatment]);
    }

    private static DecompositionResult Wrote(IEnumerable<object> created, IEnumerable<object>? updated = null)
    {
        var result = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        result.CreatedRecords.AddRange(created);
        result.UpdatedRecords.AddRange(updated ?? []);
        return result;
    }

    /// <summary>
    /// The record whose uuid names the created treatment is the one a read resolves the upload's
    /// legacy id to, so the create names the treatment as a later read does.
    /// </summary>
    public static TheoryData<string, object[], int> ServedRecords()
    {
        var bolus = new Bolus { Id = Guid.NewGuid() };
        var carbs = new CarbIntake { Id = Guid.NewGuid() };
        var bgCheck = new BGCheck { Id = Guid.NewGuid() };
        var note = new Note { Id = Guid.NewGuid() };
        var deviceEvent = new DeviceEvent { Id = Guid.NewGuid() };
        var calculation = new BolusCalculation { Id = Guid.NewGuid() };
        var tempBasal = new TempBasal { Id = Guid.NewGuid() };

        return new()
        {
            { "temp basal with a note", [note, tempBasal], 1 },
            { "note", [note], 0 },
            { "bolus with a note", [note, bolus], 1 },
            { "meal", [carbs, bolus], 1 },
            { "bg check with a note", [note, bgCheck], 1 },
            { "device event with a note", [note, deviceEvent], 1 },
            { "bolus wizard", [calculation, bolus], 1 },
            { "bolus wizard without a dose", [note, calculation], 1 },
        };
    }

    [Theory]
    [MemberData(nameof(ServedRecords))]
    public async Task CreateAsync_NamesTheTreatmentByTheRecordTheReadServesItAs(
        string shape, object[] written, int servedIndex)
    {
        var treatment = new Treatment { Id = "legacy-1", Mills = 1000, EventType = "Note" };

        var result = await CreateWithAsync(treatment, Wrote(written));

        result.Should().ContainSingle().Which.Id
            .Should().Be(((IV4Record)written[servedIndex]).Id.ToString(), shape);
    }

    [Fact]
    public async Task CreateAsync_NamesAResentTreatmentByTheRecordItUpdated()
    {
        var note = new Note { Id = Guid.NewGuid() };

        var result = await CreateWithAsync(
            new Treatment { Id = "legacy-1", Mills = 1000, EventType = "Note" }, Wrote([], [note]));

        result.Should().ContainSingle().Which.Id.Should().Be(note.Id.ToString());
    }

    [Fact]
    public async Task CreateAsync_ProjectsATempBasalAsTheReadServesIt()
    {
        var tempBasal = new TempBasal
        {
            Id = Guid.NewGuid(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime,
            Rate = 0.4,
        };

        var result = await CreateWithAsync(
            new Treatment { Id = "legacy-1", Mills = 1000, EventType = "Temp Basal" }, Wrote([tempBasal]));

        var served = result.Should().ContainSingle().Subject;
        served.Id.Should().Be(tempBasal.Id.ToString());
        served.Rate.Should().Be(0.4);
    }

    [Fact]
    public async Task CreateAsync_LeavesOutATreatmentRefusedByTheUsersDelete()
    {
        var refused = new Treatment { Id = "legacy-1", Mills = 1000, EventType = "Correction Bolus" };
        var written = new Treatment { Id = "legacy-2", Mills = 2000, EventType = "Note" };
        var note = new Note { Id = Guid.NewGuid() };
        var deletedBolus = Guid.NewGuid();
        var refusal = new DecompositionResult { SkippedDeleted = 1 };
        refusal.RefusedRecords.Add(new RefusedRecord(typeof(Bolus), deletedBolus));
        _decomposer
            .Setup(d => d.DecomposeAsync(refused, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(refusal);
        _decomposer
            .Setup(d => d.DecomposeAsync(written, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Wrote([note]));

        var result = await _service.CreateAsync([refused, written]);

        result.Should().ContainSingle().Which.Id.Should().Be(note.Id.ToString());
        result.SkippedDeleted.Should().Be(1);
        result.Settled.Select(t => t.Id).Should().Equal(
            [deletedBolus.ToString(), note.Id.ToString()],
            "a refused treatment keeps its place in the reply, named as the deleted record was served");
    }

    /// <summary>
    /// A refused temp basal is named as the temp basal read serves it: by an ObjectId legacy id, else
    /// by the deleted row's id.
    /// </summary>
    [Theory]
    [InlineData("65a1b2c3d4e5f60718293a5c", true)]
    [InlineData("syn-temp-basal", false)]
    public async Task CreateAsync_NamesARefusedTempBasalAsTheReadServedIt(string legacyId, bool servedByLegacyId)
    {
        var deletedTempBasal = Guid.NewGuid();
        var refusal = new DecompositionResult { SkippedDeleted = 1 };
        refusal.RefusedRecords.Add(new RefusedRecord(typeof(TempBasal), deletedTempBasal));

        var result = await CreateWithAsync(
            new Treatment { Id = legacyId, Mills = 1000, EventType = "Temp Basal" }, refusal);

        result.Should().BeEmpty();
        result.Settled.Should().ContainSingle().Which.Id
            .Should().Be(servedByLegacyId ? legacyId : deletedTempBasal.ToString());
    }

    [Fact]
    public async Task CreateAsync_LeavesTheSubmittedTreatmentUntouched()
    {
        var treatment = new Treatment { Id = "legacy-1", Mills = 1000, EventType = "Note" };
        var note = new Note { Id = Guid.NewGuid(), LegacyId = "legacy-1" };

        var result = await CreateWithAsync(treatment, Wrote([note]));

        treatment.Id.Should().Be("legacy-1");
        treatment.LegacyId.Should().BeNull();
        var served = result.Should().ContainSingle().Subject;
        served.Should().NotBeSameAs(treatment);
        served.LegacyId.Should().Be("legacy-1");
    }

    [Fact]
    public async Task CreateAsync_KeepsATreatmentPartlyRefusedByTheUsersDelete()
    {
        var carbs = new CarbIntake { Id = Guid.NewGuid() };
        var partial = Wrote([carbs]);
        partial.SkippedDeleted = 1;

        var result = await CreateWithAsync(
            new Treatment { Id = "legacy-1", Mills = 1000, EventType = "Meal Bolus" }, partial);

        result.Should().ContainSingle().Which.Id.Should().Be(carbs.Id.ToString());
        result.SkippedDeleted.Should().Be(1);
    }

    [Fact]
    public async Task DeleteAsync_ByStoredLegacyId_DeletesEverySiblingThroughThePipeline()
    {
        _bolusRepo
            .Setup(r => r.GetByLegacyIdAsync("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Bolus { Id = Guid.CreateVersion7(), LegacyId = "t1" });
        _pipeline
            .Setup(p => p.DeleteByLegacyIdAsync<Treatment>("t1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var result = await _service.DeleteAsync("t1");

        result.Should().NotBeNull();
        _pipeline.Verify(p => p.DeleteByLegacyIdAsync<Treatment>("t1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_NothingStoredUnderTheId_DeletesNothing()
    {
        var result = await _service.DeleteAsync("t1");

        result.Should().BeNull();
        _pipeline.Verify(
            p => p.DeleteByLegacyIdAsync<Treatment>(It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateAsync_ReturnsTheIdReadsServeForTheStoredRecord()
    {
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        var treatment = new Treatment { Id = syncIdentifier, Mills = 1000, EventType = "Carb Correction", Carbs = 20 };
        var carb = new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = syncIdentifier };
        var result = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        result.CreatedRecords.Add(carb);
        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var created = await _service.CreateAsync([treatment]);

        created.Should().ContainSingle().Which.Id.Should().Be(carb.Id.ToString());
        System.Text.Json.JsonSerializer.SerializeToElement(created[0]).GetProperty("_id").GetString()
            .Should().Be(MongoObjectId.FromGuid(carb.Id));
    }

    /// <summary>
    /// A create whose decomposition updated a stored record rather than inserting one is marked, so
    /// it is announced as an update; one that inserted is not, and one the user's deletion refused is
    /// neither written nor marked.
    /// </summary>
    [Fact]
    public async Task CreateAsync_MarksATreatmentThatUpdatedAStoredRecordAsUpdated()
    {
        var resent = new Treatment { Id = "65a1b2c3d4e5f60718293a4b", Mills = 1000, EventType = "Correction Bolus", Insulin = 1 };
        var fresh = new Treatment { Id = "65a1b2c3d4e5f60718293a4c", Mills = 2000, EventType = "Note", Notes = "kept" };
        var refused = new Treatment { Id = "65a1b2c3d4e5f60718293a4d", Mills = 3000, EventType = "Correction Bolus", Insulin = 2 };
        var resentResult = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        resentResult.UpdatedRecords.Add(new Bolus { Id = Guid.CreateVersion7(), LegacyId = resent.Id });
        var freshResult = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        freshResult.CreatedRecords.Add(new Note { Id = Guid.CreateVersion7(), LegacyId = fresh.Id });
        _decomposer
            .Setup(d => d.DecomposeAsync(resent, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resentResult);
        _decomposer
            .Setup(d => d.DecomposeAsync(fresh, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(freshResult);
        _decomposer
            .Setup(d => d.DecomposeAsync(refused, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { SkippedDeleted = 1 });

        var created = await _service.CreateAsync([resent, fresh, refused]);

        created.Should().HaveCount(2);
        created.Settled.Should().HaveCount(3);
        created.Updated.Should().ContainSingle().Which.Should().BeSameAs(created[0]);
    }

    [Fact]
    public async Task CreateAsync_MealBolus_ReturnsTheBolusIdTheMealIsReadBackUnder()
    {
        var treatment = new Treatment { Id = "meal-1", Mills = 1000, EventType = "Meal Bolus", Insulin = 2, Carbs = 30, Notes = "lunch" };
        var bolus = new Bolus { Id = Guid.CreateVersion7(), LegacyId = "meal-1" };
        var result = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        result.CreatedRecords.Add(new Note { Id = Guid.CreateVersion7(), LegacyId = "meal-1" });
        result.CreatedRecords.Add(new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = "meal-1" });
        result.UpdatedRecords.Add(bolus);
        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var created = await _service.CreateAsync([treatment]);

        created.Should().ContainSingle().Which.Id.Should().Be(bolus.Id.ToString());
    }

    [Fact]
    public async Task CreateAsync_NothingProjectedWritten_KeepsTheClientId()
    {
        var treatment = new Treatment { Id = "override-1", Mills = 1000, EventType = "Temporary Override" };
        var result = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        result.CreatedRecords.Add(new StateSpan { Id = Guid.CreateVersion7().ToString(), OriginalId = "override-1" });
        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var created = await _service.CreateAsync([treatment]);

        created.Should().ContainSingle().Which.Id.Should().Be("override-1");
    }

    [Fact]
    public async Task GetByIdAsync_GuidStoredOnlyAsALegacyId_ResolvesIt()
    {
        var clientId = Guid.NewGuid().ToString();
        var carb = new CarbIntake
        {
            Id = Guid.CreateVersion7(),
            LegacyId = clientId,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime,
        };
        _carbIntakeRepo.Setup(r => r.GetByLegacyIdAsync(clientId, It.IsAny<CancellationToken>())).ReturnsAsync(carb);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(1000, 1000, 100, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Treatment { Id = carb.Id.ToString(), Mills = 1000 }]);

        var result = await _service.GetByIdAsync(clientId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(carb.Id.ToString());
    }

    [Fact]
    public async Task GetByIdAsync_LegacyIdOfATreatmentWithNotes_ResolvesTheRecordTheCreateReturned()
    {
        var treatment = new Treatment { Id = "site-1", Mills = 1000, EventType = "Site Change", Notes = "left arm" };
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime;
        var deviceEvent = new DeviceEvent { Id = Guid.CreateVersion7(), LegacyId = "site-1", Timestamp = timestamp };
        var note = new Note { Id = Guid.CreateVersion7(), LegacyId = "site-1", Timestamp = timestamp };
        var result = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        result.CreatedRecords.Add(note);
        result.CreatedRecords.Add(deviceEvent);
        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        _deviceEventRepo.Setup(r => r.GetByLegacyIdAsync("site-1", It.IsAny<CancellationToken>())).ReturnsAsync(deviceEvent);
        _noteRepo.Setup(r => r.GetByLegacyIdAsync("site-1", It.IsAny<CancellationToken>())).ReturnsAsync(note);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(1000, 1000, 100, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Treatment { Id = note.Id.ToString(), Mills = 1000 },
                new Treatment { Id = deviceEvent.Id.ToString(), Mills = 1000 },
            ]);

        var created = await _service.CreateAsync([treatment]);
        var read = await _service.GetByIdAsync("site-1");

        created.Should().ContainSingle().Which.Id.Should().Be(deviceEvent.Id.ToString());
        read!.Id.Should().Be(deviceEvent.Id.ToString());
    }

    [Fact]
    public async Task DeleteAsync_IdAnOlderCreateEchoed_DeletesTheRecordStoredUnderTheClientId()
    {
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        var echoed = MongoObjectId.Coerce(syncIdentifier)!;
        _carbIntakeRepo
            .Setup(r => r.GetByLegacyIdUuidPrefixAsync(echoed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = syncIdentifier });
        _pipeline
            .Setup(p => p.DeleteByLegacyIdAsync<Treatment>(syncIdentifier, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _service.DeleteAsync(echoed);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetForUpdateAsync_IdAnOlderCreateEchoed_KeysTheTreatmentToTheStoredClientId()
    {
        const string syncIdentifier = "a1b2c3d4e5f60718293a4b5c6d7e8f90aa";
        var echoed = MongoObjectId.Coerce(syncIdentifier)!;
        var bolus = new Bolus { Id = Guid.CreateVersion7(), LegacyId = syncIdentifier };
        _bolusRepo
            .Setup(r => r.GetByLegacyIdHashAsync(echoed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(bolus);
        ProjectAs(bolus.Id);

        var existing = await _service.GetForUpdateAsync(echoed);

        existing!.Id.Should().Be(syncIdentifier);
    }

    [Fact]
    public async Task DeleteAsync_ExactLegacyIdInOneTable_WinsOverAPrefixMatchInAnother()
    {
        var prefixed = new Bolus { Id = Guid.CreateVersion7(), LegacyId = "bolus-legacy" };
        var wireId = MongoObjectId.FromGuid(prefixed.Id);
        _bolusRepo
            .Setup(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prefixed);
        _noteRepo
            .Setup(r => r.GetByLegacyIdAsync(wireId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Note { Id = Guid.CreateVersion7(), LegacyId = wireId });
        _pipeline
            .Setup(p => p.DeleteByLegacyIdAsync<Treatment>(wireId, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await _service.DeleteAsync(wireId);

        _pipeline.Verify(
            p => p.DeleteByLegacyIdAsync<Treatment>("bolus-legacy", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_ByDerivedObjectId_DeletesViaLegacyIdToRemoveSiblings()
    {
        // A meal bolus + its carb share one LegacyId; deleting by the derived wire ObjectId must
        // route through DeleteByLegacyId (removes both) rather than the single-row range delete
        // (which would orphan the carb into a phantom correction).
        var uuid = Guid.CreateVersion7();
        var wireId = MongoObjectId.FromGuid(uuid);
        var bolus = new Bolus { Id = uuid, LegacyId = "syn-meal-1" };

        _pipeline
            .Setup(p => p.DeleteByLegacyIdAsync<Treatment>(wireId, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // the ObjectId is not a stored LegacyId
        _bolusRepo
            .Setup(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bolus);
        _pipeline
            .Setup(p => p.DeleteByLegacyIdAsync<Treatment>("syn-meal-1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2); // both siblings

        var result = await _service.DeleteAsync(wireId);

        result.Should().NotBeNull();
        _pipeline.Verify(
            p => p.DeleteByLegacyIdAsync<Treatment>("syn-meal-1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetForUpdateAsync_BackfillsNullLegacyId_SoUpdateUpsertsInPlace()
    {
        // A native V4 row (LegacyId == null) resolved by a derived ObjectId must be backfilled with
        // that ObjectId so the decomposer upserts it in place instead of inserting a duplicate.
        var uuid = Guid.CreateVersion7();
        var wireId = MongoObjectId.FromGuid(uuid);
        var note = new Note { Id = uuid, LegacyId = null };

        _noteRepo
            .Setup(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(note);
        ProjectAs(uuid);

        var existing = await _service.GetForUpdateAsync(wireId);

        existing!.Id.Should().Be(wireId);
        // The backfill goes through IV4Repository<Note>.UpdateAsync (the base slot the generic
        // helper is typed against), which INoteRepository new-shadows: verify the base slot.
        _noteRepo.As<IV4Repository<Note>>().Verify(
            r => r.UpdateAsync(uuid, It.Is<Note>(n => n.LegacyId == wireId), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void ProjectAs(Guid recordId) =>
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Treatment { Id = recordId.ToString() }]);

    private void VerifyOneHashScanPerTable()
    {
        _bolusRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _carbIntakeRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _bgCheckRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _noteRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _deviceEventRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _bolusCalcRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _tempBasalRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_UnknownObjectId_ScansEachTableOnce()
    {
        (await _service.DeleteAsync("0123456789abcdef01234567")).Should().BeNull();

        VerifyOneHashScanPerTable();
    }

    [Fact]
    public async Task UpdateAsync_UnknownObjectId_ScansEachTableOnce()
    {
        (await _service.UpdateAsync("0123456789abcdef01234567", new Treatment { EventType = "Note" })).Should().BeNull();

        VerifyOneHashScanPerTable();
        _decomposer.Verify(d => d.DecomposeAsync(
            It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetForUpdateAsync_UnknownObjectId_ScansEachTableOnce()
    {
        (await _service.GetForUpdateAsync("0123456789abcdef01234567")).Should().BeNull();

        VerifyOneHashScanPerTable();
    }

    [Fact]
    public async Task UpdateAsync_ByAnEchoedId_ReadsTheResultBackByTheRecordsOwnId()
    {
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        var echoed = MongoObjectId.Coerce(syncIdentifier)!;
        var carb = new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = syncIdentifier };
        _carbIntakeRepo.Setup(r => r.GetByLegacyIdUuidPrefixAsync(echoed, It.IsAny<CancellationToken>())).ReturnsAsync(carb);
        _carbIntakeRepo.Setup(r => r.GetByIdAsync(carb.Id, It.IsAny<CancellationToken>())).ReturnsAsync(carb);
        ProjectAs(carb.Id);
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());

        var updated = await _service.UpdateAsync(echoed, new Treatment { EventType = "Carb Correction", Carbs = 25 });

        updated!.Id.Should().Be(carb.Id.ToString());
        _decomposer.Verify(d => d.DecomposeAsync(
            It.Is<Treatment>(t => t.Id == syncIdentifier && t.Carbs == 25),
            It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
        _carbIntakeRepo.Verify(r => r.GetByLegacyIdUuidPrefixAsync(echoed, It.IsAny<CancellationToken>()), Times.Once);
        _carbIntakeRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IsDeletedByUserAsync_AnyTreatmentTableHoldingATombstone_IsTrue()
    {
        _noteRepo.Setup(r => r.IsDeletedByUserAsync("t1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        (await _service.IsDeletedByUserAsync("t1")).Should().BeTrue();
        (await _service.IsDeletedByUserAsync("t2")).Should().BeFalse();
    }
}
