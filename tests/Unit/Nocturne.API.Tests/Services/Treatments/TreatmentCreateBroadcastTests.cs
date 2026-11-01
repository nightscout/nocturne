using Nocturne.API.Tests.TestDoubles;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// A treatment create is announced as reads serve it, under the <c>_id</c> REST serves for it, and only
/// when something was written (#1809). Runs the service over the real read service and event sink.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentCreateBroadcastTests
{
    private const string ClientId = "65f0a1b2c3d4e5f601234567";
    private const string Connector = "nightscout-connector";
    private static readonly DateTime At = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IPerTreatmentDecomposer> _decomposer = new Mock<IPerTreatmentDecomposer>().AnswersBatchPerTreatment();
    private readonly Mock<IV4ToLegacyProjectionService> _projection = new();
    private readonly Mock<ITempBasalRepository> _tempBasals = new();
    private readonly Mock<IBolusRepository> _boluses = new();
    private readonly Mock<ICarbIntakeRepository> _carbs = new();
    private readonly Mock<INoteRepository> _notes = new();
    private readonly Mock<IProfileActivationRepository> _activations = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly List<StorageDocumentEvent> _creates = [];
    private readonly TreatmentService _service;

    public TreatmentCreateBroadcastTests()
    {
        _broadcast
            .Setup(b => b.BroadcastStorageCreateAsync("treatments", It.IsAny<object>()))
            .Callback<string, object>((_, data) => _creates.Add((StorageDocumentEvent)data))
            .Returns(Task.CompletedTask);

        _tempBasals.AnswersServedIdsPerKey<ITempBasalRepository, TempBasal>();
        _boluses.AnswersServedIdsPerKey<IBolusRepository, Bolus>();
        _carbs.AnswersServedIdsPerKey<ICarbIntakeRepository, CarbIntake>();
        _notes.AnswersServedIdsPerKey<INoteRepository, Note>();
        _activations.AnswersServedIdsPerKey<IProfileActivationRepository, ProfileActivation>();
        _projection.AnswersSpanIdsPerId();
        LegacyTreatmentRepositoryMockExtensions.AnswersCorrelationIdsPerId(_boluses, _carbs);
        var store = new TreatmentReadService(
            _projection.Object,
            _decomposer.Object,
            _tempBasals.Object,
            _boluses.Object,
            _carbs.Object,
            new Mock<IBGCheckRepository>().AnswersServedIdsPerKey<IBGCheckRepository, BGCheck>().Object,
            _notes.Object,
            new Mock<IDeviceEventRepository>().AnswersServedIdsPerKey<IDeviceEventRepository, DeviceEvent>().Object,
            new Mock<IBolusCalculationRepository>().AnswersServedIdsPerKey<IBolusCalculationRepository, BolusCalculation>().Object,
            _activations.Object,
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);

        _service = new TreatmentService(
            store,
            _decomposer.Object,
            Mock.Of<ITreatmentCache>(),
            new SignalRTreatmentEventSink(_broadcast.Object, NullLogger<SignalRTreatmentEventSink>.Instance),
            Mock.Of<IPatientInsulinRepository>(),
            NullLogger<TreatmentService>.Instance);
    }

    /// <summary>
    /// A connector's meal arrives as two treatments, one per half. The carbs half is stored as a carb
    /// intake paired with the bolus already stored, and reads serve the pair once, under the bolus's id.
    /// </summary>
    [Fact]
    public async Task ACreatedCarbHalfIsAnnouncedAsTheMealReadsServe()
    {
        var correlation = Guid.CreateVersion7();
        var bolus = new Bolus { Id = Guid.CreateVersion7(), Timestamp = At, Insulin = 4, CorrelationId = correlation };
        var carbs = new CarbIntake
        {
            Id = Guid.CreateVersion7(), Timestamp = At, Carbs = 40, CorrelationId = correlation, LegacyId = ClientId,
        };
        Decomposes(carbs);
        _carbs.Setup(r => r.GetByIdAsync(carbs.Id, It.IsAny<CancellationToken>())).ReturnsAsync(carbs);
        _boluses.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>())).ReturnsAsync([bolus]);
        _carbs.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>())).ReturnsAsync([carbs]);
        _boluses.Setup(r => r.GetByIdAsync(bolus.Id, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        Serves(new Treatment
        {
            Id = bolus.Id.ToString(), EventType = "Meal Bolus", Insulin = 4, Carbs = 40, Mills = bolus.Mills,
        });

        var created = await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Carb Correction", Carbs = 40, Mills = carbs.Mills }]);

        var served = await _service.GetTreatmentByIdAsync(created.Single().Id!);
        var announced = _creates.Should().ContainSingle().Subject;
        Wire(announced.Doc).GetProperty("_id").GetString()
            .Should().Be(Wire(served!).GetProperty("_id").GetString())
            .And.Be(MongoObjectId.FromGuid(bolus.Id));
        Wire(announced.Doc).GetProperty("insulin").GetDouble().Should().Be(4);
    }

    /// <summary>
    /// A client that keeps the create response's id reads the same treatment back by it, so the
    /// response names the meal by its bolus, as the event and every read do.
    /// </summary>
    [Fact]
    public async Task ACreatedCarbHalfIsAnsweredWithTheIdTheMealReadsServe()
    {
        var correlation = Guid.CreateVersion7();
        var bolus = new Bolus { Id = Guid.CreateVersion7(), Timestamp = At, Insulin = 4, CorrelationId = correlation };
        var carbs = new CarbIntake
        {
            Id = Guid.CreateVersion7(), Timestamp = At, Carbs = 40, CorrelationId = correlation, LegacyId = ClientId,
        };
        Decomposes(carbs);
        _carbs.Setup(r => r.GetByIdAsync(carbs.Id, It.IsAny<CancellationToken>())).ReturnsAsync(carbs);
        _boluses.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>())).ReturnsAsync([bolus]);
        _carbs.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>())).ReturnsAsync([carbs]);
        _boluses.Setup(r => r.GetByIdAsync(bolus.Id, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        Serves(new Treatment
        {
            Id = bolus.Id.ToString(), EventType = "Meal Bolus", Insulin = 4, Carbs = 40, Mills = bolus.Mills,
        });

        var created = await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Carb Correction", Carbs = 40, Mills = carbs.Mills }]);

        var served = await _service.GetTreatmentByIdAsync(created.Single().Id!);
        var servedByCarbId = await _service.GetTreatmentByIdAsync(carbs.Id.ToString());
        var announced = _creates.Should().ContainSingle().Subject;
        Wire(created.Single()).GetProperty("_id").GetString()
            .Should().Be(MongoObjectId.FromGuid(bolus.Id))
            .And.Be(Wire(served!).GetProperty("_id").GetString())
            .And.Be(Wire(servedByCarbId!).GetProperty("_id").GetString())
            .And.Be(Wire(announced.Doc).GetProperty("_id").GetString());
    }

    /// <summary>
    /// Only the largest carb intake of a correlation is half of its Meal Bolus; a smaller one is served
    /// as a Carb Correction under its own id, so a client that deletes by the response id deletes that
    /// carb and nothing else.
    /// </summary>
    [Fact]
    public async Task ACreatedExtraCarbIsAnsweredWithItsOwnIdAndDeletedAlone()
    {
        var correlation = Guid.CreateVersion7();
        var bolus = new Bolus { Id = Guid.CreateVersion7(), Timestamp = At, Insulin = 4, CorrelationId = correlation };
        var mealCarbs = new CarbIntake
        {
            Id = Guid.CreateVersion7(), Timestamp = At, Carbs = 40, CorrelationId = correlation,
        };
        var extra = new CarbIntake
        {
            Id = Guid.CreateVersion7(), Timestamp = At, Carbs = 10, CorrelationId = correlation, LegacyId = ClientId,
        };
        Decomposes(extra);
        _carbs.Setup(r => r.GetByIdAsync(extra.Id, It.IsAny<CancellationToken>())).ReturnsAsync(extra);
        _carbs.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>()))
            .ReturnsAsync([mealCarbs, extra]);
        _boluses.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>())).ReturnsAsync([bolus]);
        Serves(new Treatment
        {
            Id = extra.Id.ToString(), EventType = "Carb Correction", Carbs = 10, Mills = extra.Mills,
        });
        _decomposer
            .Setup(d => d.DeleteByLegacyIdAsync(ClientId, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var created = await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Carb Correction", Carbs = 10, Mills = extra.Mills }]);
        var deleted = await _service.DeleteTreatmentAsync(created.Single().Id!);

        Wire(created.Single()).GetProperty("_id").GetString().Should().Be(MongoObjectId.FromGuid(extra.Id));
        Wire(_creates.Should().ContainSingle().Subject.Doc).GetProperty("_id").GetString()
            .Should().Be(MongoObjectId.FromGuid(extra.Id));
        deleted.Should().BeTrue();
        _decomposer.Verify(
            d => d.DeleteByLegacyIdAsync(It.IsAny<string>(), WriteOrigin.Live, It.IsAny<CancellationToken>()),
            Times.Once);
        _decomposer.Verify(
            d => d.DeleteByLegacyIdAsync(ClientId, WriteOrigin.Live, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The Nightscout write-back sink skips a treatment by its data source, so a connector's own
    /// temp target or profile switch announced without one would be posted back upstream.
    /// </summary>
    [Fact]
    public async Task AConnectorsTempTargetIsAnnouncedWithItsDataSource()
    {
        var spanId = Guid.CreateVersion7().ToString();
        Decomposes(new StateSpan { OriginalId = ClientId, Category = StateSpanCategory.TemporaryTarget });
        _projection
            .Setup(p => p.GetProjectedStateSpanTreatmentAsync(It.IsIn(ClientId, spanId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Treatment { Id = spanId, EventType = "Temporary Target", Mills = 1_790_000_000_000 });

        await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Temporary Target", Mills = 1_790_000_000_000, DataSource = Connector }]);

        _creates.Should().ContainSingle().Which.Doc.Should().BeOfType<Treatment>().Which.DataSource.Should().Be(Connector);
    }

    [Fact]
    public async Task AConnectorsProfileSwitchIsAnnouncedWithItsDataSource()
    {
        var activation = new ProfileActivation
        {
            Id = Guid.CreateVersion7(), Timestamp = At, LegacyId = ClientId, DataSource = Connector,
        };
        Decomposes(activation);
        _activations.Setup(r => r.GetByIdAsync(activation.Id, It.IsAny<CancellationToken>())).ReturnsAsync(activation);
        Serves(new Treatment { Id = activation.Id.ToString(), EventType = "Profile Switch", Mills = activation.Mills });

        await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Profile Switch", Mills = activation.Mills, DataSource = Connector }]);

        _creates.Should().ContainSingle().Which.Doc.Should().BeOfType<Treatment>().Which.DataSource.Should().Be(Connector);
    }

    [Fact]
    public async Task ACareportalTempTargetIsAnnouncedWithoutADataSource()
    {
        var spanId = Guid.CreateVersion7().ToString();
        Decomposes(new StateSpan { OriginalId = ClientId, Category = StateSpanCategory.TemporaryTarget });
        _projection
            .Setup(p => p.GetProjectedStateSpanTreatmentAsync(It.IsIn(ClientId, spanId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Treatment { Id = spanId, EventType = "Temporary Target", Mills = 1_790_000_000_000 });

        await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Temporary Target", Mills = 1_790_000_000_000, EnteredBy = "careportal" }]);

        _creates.Should().ContainSingle().Which.Doc.Should().BeOfType<Treatment>().Which.DataSource.Should().BeNull();
    }

    [Fact]
    public async Task AServedDataSourceIsNotOverwrittenByTheUploadsOwn()
    {
        var activation = new ProfileActivation
        {
            Id = Guid.CreateVersion7(), Timestamp = At, LegacyId = ClientId, DataSource = "glooko-connector",
        };
        Decomposes(activation);
        _activations.Setup(r => r.GetByIdAsync(activation.Id, It.IsAny<CancellationToken>())).ReturnsAsync(activation);
        Serves(new Treatment
        {
            Id = activation.Id.ToString(), EventType = "Profile Switch", Mills = activation.Mills, DataSource = "glooko-connector",
        });

        await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Profile Switch", Mills = activation.Mills, DataSource = Connector }]);

        _creates.Should().ContainSingle().Which.Doc.Should().BeOfType<Treatment>().Which.DataSource.Should().Be("glooko-connector");
    }

    [Fact]
    public async Task ATreatmentRefusedByAUserTombstoneIsNotAnnounced()
    {
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { SkippedDeleted = 1 });

        var created = await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Correction Bolus", Insulin = 2, Mills = 1_790_000_000_000 }]);

        created.SkippedDeleted.Should().Be(1);
        _creates.Should().BeEmpty();
    }

    /// <summary>
    /// A batch is read back at the instants its treatments start at, in one read, so a Loop upload or
    /// a connector's poll after a gap costs the same reads as a single treatment, and a treatment from
    /// a client with a bad clock does not read the years between it and the rest.
    /// </summary>
    [Fact]
    public async Task ABatchReadsBackOnlyTheInstantsItsTreatmentsStartAt()
    {
        var boluses = Enumerable.Range(0, 25)
            .Select(i => new Bolus { Id = Guid.CreateVersion7(), Timestamp = At.AddMinutes(i * 5), Insulin = 1 + i })
            .Append(new Bolus { Id = Guid.CreateVersion7(), Timestamp = DateTime.UnixEpoch, Insulin = 30 })
            .ToList();
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Treatment sent, WriteOrigin _, CancellationToken _) =>
            {
                var result = new DecompositionResult();
                result.CreatedRecords.Add(boluses.Single(b => b.Mills == sent.Mills));
                return result;
            });
        ServesAtInstants([.. boluses.Select(b => new Treatment
        {
            Id = b.Id.ToString(), EventType = "Correction Bolus", Insulin = b.Insulin, Mills = b.Mills,
        })]);

        await _service.CreateTreatmentsAsync(
            [.. boluses.Select(b => new Treatment { EventType = "Correction Bolus", Insulin = b.Insulin, Mills = b.Mills })]);

        _creates.Select(e => ((Treatment)e.Doc).Id).Should().Equal(boluses.Select(b => b.Id.ToString()));
        _creates.Select(e => ((Treatment)e.Doc).Insulin).Should().Equal(boluses.Select(b => (double?)b.Insulin));
        InstantsRead(_projection.Invocations).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(boluses.Select(b => b.Mills));
        _projection.Invocations
            .Should().NotContain(i => i.Method.Name == nameof(IV4ToLegacyProjectionService.GetProjectedTreatmentsAsync));
        _boluses.Invocations.Should().BeEmpty();
    }

    /// <summary>
    /// Every kind of treatment in one batch, years apart, is announced as a read by its id serves it
    /// once the batch is written, in the order sent, for one read of the instants they start at.
    /// </summary>
    [Fact]
    public async Task AMixedBatchIsAnnouncedAsEachReadsBackByIdForOneRead()
    {
        var meal = Guid.CreateVersion7();
        var tempBasal = new TempBasal
        {
            Id = Guid.CreateVersion7(), StartTimestamp = At, EndTimestamp = At.AddMinutes(30), Rate = 1,
        };
        var bolus = new Bolus { Id = Guid.CreateVersion7(), Timestamp = At.AddYears(-1), Insulin = 4, CorrelationId = meal };
        var carbs = new CarbIntake { Id = Guid.CreateVersion7(), Timestamp = bolus.Timestamp, Carbs = 40, CorrelationId = meal };
        var note = new Note { Id = Guid.CreateVersion7(), Timestamp = At.AddDays(30), Text = "Site change" };
        var correction = new Bolus { Id = Guid.CreateVersion7(), Timestamp = DateTime.UnixEpoch, Insulin = 2, LegacyId = ClientId };
        var correctionNote = new Note
        {
            Id = Guid.CreateVersion7(), Timestamp = DateTime.UnixEpoch, Text = "Pre-meal", LegacyId = ClientId,
        };
        const string targetKey = "65f0a1b2c3d4e5f601234568";
        var spanId = Guid.CreateVersion7().ToString();
        var targetMills = new DateTimeOffset(At.AddYears(1)).ToUnixTimeMilliseconds();
        var writes = new Dictionary<(string?, long), object[]>
        {
            [("Temp Basal", tempBasal.StartMills)] = [tempBasal],
            [("Meal Bolus", bolus.Mills)] = [bolus],
            [("Carb Correction", carbs.Mills)] = [carbs],
            [("Note", note.Mills)] = [note],
            [("Correction Bolus", correction.Mills)] = [correction, correctionNote],
            [("Temporary Target", targetMills)] =
                [new StateSpan { OriginalId = targetKey, Category = StateSpanCategory.TemporaryTarget }],
        };
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Treatment sent, WriteOrigin _, CancellationToken _) =>
            {
                var result = new DecompositionResult();
                result.CreatedRecords.AddRange(writes[(sent.EventType, sent.Mills)]);
                return result;
            });
        _tempBasals.Setup(r => r.GetByIdAsync(tempBasal.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tempBasal);
        StoresTempBasals(() => [tempBasal]);
        _boluses.Setup(r => r.GetByIdAsync(bolus.Id, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        _boluses.Setup(r => r.GetByIdAsync(correction.Id, It.IsAny<CancellationToken>())).ReturnsAsync(correction);
        _boluses.Setup(r => r.GetByCorrelationIdAsync(meal, It.IsAny<CancellationToken>())).ReturnsAsync([bolus]);
        _carbs.Setup(r => r.GetByCorrelationIdAsync(meal, It.IsAny<CancellationToken>())).ReturnsAsync([carbs]);
        _notes.Setup(r => r.GetByIdAsync(note.Id, It.IsAny<CancellationToken>())).ReturnsAsync(note);
        _projection
            .Setup(p => p.GetProjectedStateSpanTreatmentAsync(It.IsIn(targetKey, spanId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Treatment { Id = spanId, EventType = "Temporary Target", Mills = targetMills });
        ServesAtInstants(
            new Treatment { Id = bolus.Id.ToString(), EventType = "Meal Bolus", Insulin = 4, Carbs = 40, Mills = bolus.Mills },
            new Treatment { Id = note.Id.ToString(), EventType = "Note", Notes = "Site change", Mills = note.Mills },
            new Treatment { Id = correction.Id.ToString(), EventType = "Correction Bolus", Insulin = 2, Notes = "Pre-meal", Mills = 0 },
            new Treatment { Id = spanId, EventType = "Temporary Target", Mills = targetMills });

        var created = await _service.CreateTreatmentsAsync(
        [
            new Treatment { EventType = "Temp Basal", Absolute = 1, Mills = tempBasal.StartMills },
            new Treatment { EventType = "Meal Bolus", Insulin = 4, Mills = bolus.Mills },
            new Treatment { EventType = "Carb Correction", Carbs = 40, Mills = carbs.Mills },
            new Treatment { EventType = "Note", Notes = "Site change", Mills = note.Mills },
            new Treatment { Id = ClientId, EventType = "Correction Bolus", Insulin = 2, Notes = "Pre-meal", Mills = correction.Mills },
            new Treatment { Id = targetKey, EventType = "Temporary Target", Mills = targetMills },
        ]);
        var readsAtCreate = _projection.Invocations.ToList();
        var lookupsAtCreate = _notes.Invocations.Count
            + _tempBasals.Invocations.Count(i => i.Method.Name == nameof(ITempBasalRepository.GetByIdAsync))
            + _boluses.Invocations.Count(i => i.Method.Name == nameof(IBolusRepository.GetByIdAsync));

        var servedById = new List<Treatment>();
        foreach (var treatment in created)
            servedById.Add((await _service.GetTreatmentByIdAsync(treatment.Id!))!);

        _creates.Select(e => Wire(e.Doc).GetRawText())
            .Should().Equal(servedById.DistinctBy(t => t.Id).Select(t => Wire(t).GetRawText()));
        _creates.Select(e => ((Treatment)e.Doc).Id)
            .Should().Equal(tempBasal.Id.ToString(), bolus.Id.ToString(), note.Id.ToString(), correction.Id.ToString(), spanId);
        InstantsRead(readsAtCreate).Should().ContainSingle()
            .Which.Distinct().Should().BeEquivalentTo([bolus.Mills, note.Mills, 0L, targetMills]);
        readsAtCreate
            .Should().NotContain(i => i.Method.Name == nameof(IV4ToLegacyProjectionService.GetProjectedTreatmentsAsync));
        lookupsAtCreate.Should().Be(0);
    }

    /// <summary>
    /// A Loop or AAPS re-send can carry one temp basal twice in an upload. Reads serve the row the
    /// second wrote, so that is what is announced.
    /// </summary>
    [Fact]
    public async Task ATempBasalSentTwiceInABatchIsAnnouncedOnceAsTheBatchLeftIt()
    {
        var id = Guid.CreateVersion7();
        var first = new TempBasal { Id = id, StartTimestamp = At, EndTimestamp = At.AddMinutes(30), Rate = 1 };
        var resent = new TempBasal { Id = id, StartTimestamp = At, EndTimestamp = At.AddMinutes(60), Rate = 2 };
        var created = new DecompositionResult();
        created.CreatedRecords.Add(first);
        var updated = new DecompositionResult();
        updated.UpdatedRecords.Add(resent);
        _decomposer
            .SetupSequence(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created)
            .ReturnsAsync(updated);
        StoresTempBasals(() => [resent]);

        await _service.CreateTreatmentsAsync(
        [
            new Treatment { EventType = "Temp Basal", Absolute = 1, Duration = 30, Mills = first.StartMills },
            new Treatment { EventType = "Temp Basal", Absolute = 2, Duration = 60, Mills = first.StartMills },
        ]);

        var announced = _creates.Should().ContainSingle().Which.Doc.Should().BeOfType<Treatment>().Subject;
        (announced.Id, announced.Absolute, announced.Duration).Should().Be((id.ToString(), (double?)2, (double?)60));
    }

    /// <summary>
    /// A Profile Switch later in the batch re-resolves the rate of a percent temp basal it covers from
    /// its schedule. Reads serve the rate it resolved, so that is what is announced.
    /// </summary>
    [Fact]
    public async Task ATempBasalIsAnnouncedWithTheRateALaterProfileSwitchInTheBatchResolved()
    {
        var id = Guid.CreateVersion7();
        TempBasal PercentOfSchedule(double scheduled) => new()
        {
            Id = id, StartTimestamp = At, EndTimestamp = At.AddMinutes(30), Percent = 50,
            Rate = scheduled / 2, ScheduledRate = scheduled, RateFromSchedule = true,
        };
        var stored = PercentOfSchedule(scheduled: 1);
        var activation = new ProfileActivation { Id = Guid.CreateVersion7(), Timestamp = At.AddHours(-1), LegacyId = ClientId };
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Treatment sent, WriteOrigin _, CancellationToken _) =>
            {
                var result = new DecompositionResult();
                if (sent.EventType == "Temp Basal")
                {
                    result.CreatedRecords.Add(PercentOfSchedule(scheduled: 1));
                }
                else
                {
                    stored = PercentOfSchedule(scheduled: 2);
                    result.CreatedRecords.Add(activation);
                }
                return result;
            });
        StoresTempBasals(() => [stored]);
        _tempBasals.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => stored);
        Serves(new Treatment { Id = activation.Id.ToString(), EventType = "Profile Switch", Mills = activation.Mills });

        var created = await _service.CreateTreatmentsAsync(
        [
            new Treatment { EventType = "Temp Basal", Percent = 50, Duration = 30, Mills = stored.StartMills },
            new Treatment { Id = ClientId, EventType = "Profile Switch", Mills = activation.Mills },
        ]);

        var announced = _creates.Select(e => (Treatment)e.Doc).First(t => t.EventType == "Temp Basal");
        announced.DeliveredRate.Should().Be(1);
        Wire(announced).GetRawText()
            .Should().Be(Wire((await _service.GetTreatmentByIdAsync(created[0].Id!))!).GetRawText());
    }

    [Fact]
    public async Task ABatchOfTempBasalsIsReadBackInOneRead()
    {
        var tempBasals = Enumerable.Range(0, 25)
            .Select(i => new TempBasal
            {
                Id = Guid.CreateVersion7(), StartTimestamp = At.AddMinutes(i * 5), EndTimestamp = At.AddMinutes(i * 5 + 30), Rate = 1,
            })
            .ToList();
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Treatment sent, WriteOrigin _, CancellationToken _) =>
            {
                var result = new DecompositionResult();
                result.CreatedRecords.Add(tempBasals.Single(t => t.StartMills == sent.Mills));
                return result;
            });
        StoresTempBasals(() => tempBasals);

        await _service.CreateTreatmentsAsync(
            [.. tempBasals.Select(t => new Treatment { EventType = "Temp Basal", Absolute = 1, Mills = t.StartMills })]);

        _creates.Select(e => ((Treatment)e.Doc).Id).Should().Equal(tempBasals.Select(t => t.Id.ToString()));
        _tempBasals.Invocations.Should().ContainSingle()
            .Which.Method.Name.Should().Be(nameof(ITempBasalRepository.GetByIdsAsync));
        _projection.Invocations.Should().BeEmpty();
    }

    private void Decomposes(params object[] records)
    {
        var result = new DecompositionResult();
        result.CreatedRecords.AddRange(records);
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }

    private void Serves(Treatment treatment) => ServesAtInstants(treatment);

    /// <summary>Serves the temp basals <paramref name="rows"/> returns at the time of the read to a read by their ids.</summary>
    private void StoresTempBasals(Func<IReadOnlyList<TempBasal>> rows) =>
        _tempBasals
            .Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => [.. rows().Where(t => ids.Contains(t.Id))]);

    /// <summary>Serves each of <paramref name="treatments"/> to a read of the instant it starts at.</summary>
    private void ServesAtInstants(params Treatment[] treatments) =>
        _projection
            .Setup(p => p.GetProjectedTreatmentsAtAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<long> instants, CancellationToken _) =>
                treatments.Where(t => instants.Contains(t.Mills)).ToList());

    private static IEnumerable<IReadOnlyCollection<long>> InstantsRead(IEnumerable<IInvocation> reads) =>
        reads
            .Where(i => i.Method.Name == nameof(IV4ToLegacyProjectionService.GetProjectedTreatmentsAtAsync))
            .Select(i => (IReadOnlyCollection<long>)i.Arguments[0]);

    private static JsonElement Wire(object doc) => JsonSerializer.SerializeToElement(doc, doc.GetType());
}
