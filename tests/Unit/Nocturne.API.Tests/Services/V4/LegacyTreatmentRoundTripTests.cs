using Nocturne.API.Tests.Infrastructure;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A legacy treatment through the v1/v3 service path, decomposed into V4 records and read back:
/// one document per upload, under the event type it was uploaded with, as Nightscout serves it.
/// </summary>
[Trait("Category", "Unit")]
public class LegacyTreatmentRoundTripTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string At = "2026-01-01T12:00:00.000Z";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TreatmentReadService _store;
    private readonly TreatmentService _service;
    private readonly BolusRepository _bolusRepo;
    private readonly NoteRepository _noteRepo;

    public LegacyTreatmentRoundTripTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, SqliteNpgsqlJson.Translate);
        _context = _db.CreateContext();

        IAuditContext caller = new AuditContext
        {
            SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
            SubjectName = "uploader",
            AuthType = "ApiSecret",
            Endpoint = "POST /api/v1/treatments",
        };

        var dedup = new Mock<IDeduplicationService>().Object;
        var ctxFactory = new TestNocturneDbContextFactory(_context);
        var bolusRepo = _bolusRepo = new BolusRepository(ctxFactory, dedup, caller, NullLogger<BolusRepository>.Instance);
        var carbRepo = new CarbIntakeRepository(ctxFactory, dedup, caller, NullLogger<CarbIntakeRepository>.Instance);
        var bgCheckRepo = new BGCheckRepository(ctxFactory, dedup, caller, NullLogger<BGCheckRepository>.Instance);
        var noteRepo = _noteRepo = new NoteRepository(ctxFactory, dedup, caller, NullLogger<NoteRepository>.Instance);
        var deviceEventRepo = new DeviceEventRepository(ctxFactory, dedup, caller, NullLogger<DeviceEventRepository>.Instance);
        var bolusCalcRepo = new BolusCalculationRepository(ctxFactory, dedup, caller, NullLogger<BolusCalculationRepository>.Instance);

        var tempBasalRepo = new TempBasalRepository(ctxFactory, dedup, caller, NullLogger<TempBasalRepository>.Instance);

        var foods = new Mock<ITreatmentFoodService>();
        foods
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var devices = new Mock<IDeviceService>();
        devices
            .Setup(s => s.ResolveAsync(
                It.IsAny<V4Models.DeviceCategory>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        var decomposer = new TreatmentDecomposer(
            _context.AsFactory(),
            bolusRepo, tempBasalRepo,
            carbRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
            Mock.Of<IStateSpanService>(),
            ProfileActivationTestDeps.Repository(_context),
            ProfileActivationTestDeps.Writer(_context),
            foods.Object,
            devices.Object,
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            TempBasalTestDeps.Unscheduled(),
            caller,
            NullLogger<TreatmentDecomposer>.Instance);

        var projection = new V4ToLegacyProjectionService(
            bolusRepo, carbRepo, bgCheckRepo, noteRepo, deviceEventRepo,
            tempBasalRepo, bolusCalcRepo,
            foods.Object,
            _context.AsFactory(),
            NullLogger<V4ToLegacyProjectionService>.Instance);

        var pipeline = new DecompositionPipeline(
            new ServiceCollection().AddSingleton<IDecomposer<Treatment>>(decomposer).BuildServiceProvider(),
            NullLogger<DecompositionPipeline>.Instance);

        _store = new TreatmentReadService(
            projection, decomposer, pipeline,
            tempBasalRepo, bolusRepo, carbRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
            Mock.Of<IProfileActivationRepository>(),
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);

        _service = new TreatmentService(
            _store, decomposer, Mock.Of<ITreatmentCache>(), Mock.Of<IDataEventSink<Treatment>>(),
            Mock.Of<IPatientInsulinRepository>(), NullLogger<TreatmentService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Treatment> UploadAsync(string json) =>
        (await _service.CreateTreatmentsAsync([JsonSerializer.Deserialize<Treatment>(json)!])).Single();

    private async Task<List<Treatment>> ReadAsync(string? find = null) =>
        (await _service.GetTreatmentsAsync(find, count: 100)).ToList();

    private static string Upload(
        string eventType, double? insulin = null, double? carbs = null, string? notes = null,
        double? glucose = null, string at = At) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["eventType"] = eventType,
            ["created_at"] = at,
            ["insulin"] = insulin,
            ["carbs"] = carbs,
            ["glucose"] = glucose,
            ["notes"] = notes,
        }.Where(kv => kv.Value is not null).ToDictionary());

    #region Notes are served on the treatment they were uploaded with (#2308)

    [Theory]
    [InlineData("Correction Bolus", 2.0, null)]
    [InlineData("Meal Bolus", 3.5, 40.0)]
    [InlineData("Carb Correction", null, 20.0)]
    public async Task RecordWithNotes_ReadsBackAsOneTreatmentCarryingItsNotes(
        string eventType, double? insulin, double? carbs)
    {
        await UploadAsync(Upload(eventType, insulin, carbs, notes: "after the walk"));

        var served = (await ReadAsync()).Should().ContainSingle().Subject;
        served.EventType.Should().Be(eventType);
        served.Insulin.Should().Be(insulin);
        served.Carbs.Should().Be(carbs);
        served.Notes.Should().Be("after the walk");
    }

    [Fact]
    public async Task BgCheckWithNotes_ReadsBackAsOneTreatmentCarryingItsNotes()
    {
        await UploadAsync(Upload("BG Check", glucose: 120, notes: "finger stick"));

        var served = (await ReadAsync()).Should().ContainSingle().Subject;
        served.EventType.Should().Be("BG Check");
        served.Notes.Should().Be("finger stick");
    }

    [Fact]
    public async Task NoteTreatment_StillReadsBackAsItsOwnTreatment()
    {
        await UploadAsync(Upload("Note", notes: "site sore"));

        var served = (await ReadAsync()).Should().ContainSingle().Subject;
        (served.EventType, served.Notes).Should().Be(("Note", "site sore"));
    }

    [Fact]
    public async Task RecordWithNotes_KeepsItsV4Note()
    {
        await UploadAsync(Upload("Correction Bolus", insulin: 2.0, notes: "after the walk"));

        await using var ctx = _db.CreateContext();
        var bolus = ctx.Boluses.Single();
        var note = ctx.Notes.Should().ContainSingle().Subject;
        (note.Text, note.CorrelationId).Should().Be(("after the walk", bolus.CorrelationId));
    }

    [Fact]
    public async Task RecordsWithNotes_CountAsOneTreatmentEach()
    {
        await UploadAsync(Upload("Correction Bolus", insulin: 2.0, notes: "one"));
        await UploadAsync(Upload("Carb Correction", carbs: 30, notes: "two", at: "2026-01-01T11:00:00.000Z"));
        await UploadAsync(Upload("Note", notes: "three", at: "2026-01-01T10:00:00.000Z"));

        (await _store.CountAsync()).Should().Be(3);
        (await _store.CountAsync("find[notes][$exists]=true")).Should().Be(3);
    }

    [Fact]
    public async Task RecordWithNotes_HistoryServesOneTreatmentCarryingItsNotes()
    {
        await UploadAsync(Upload("Correction Bolus", insulin: 2.0, notes: "after the walk"));

        var history = (await _service.GetTreatmentsModifiedSinceAsync(0)).ToList();
        var served = history.Where(t => t.IsValid != false).Should().ContainSingle().Subject;
        (served.EventType, served.Insulin, served.Notes).Should().Be(("Correction Bolus", 2.0, "after the walk"));
    }

    /// <summary>
    /// A client that synced before notes were served on their record holds the Note as a treatment
    /// of its own; history tombstones it whenever it moves.
    /// </summary>
    [Fact]
    public async Task RecordWithNotes_HistoryTombstonesTheNoteUnderItsOwnId()
    {
        await UploadAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["eventType"] = "Correction Bolus", ["created_at"] = At, ["insulin"] = 2.0,
            ["notes"] = "after the walk", ["syncIdentifier"] = "loop-sync-1",
        }));

        var history = (await _service.GetTreatmentsModifiedSinceAsync(0)).ToList();

        var tombstone = history.Where(t => t.IsValid == false).Should().ContainSingle().Subject;
        tombstone.Id.Should().Be((await NoteIdAsync()).ToString());
        (tombstone.EventType, tombstone.SyncIdentifier, tombstone.Insulin, tombstone.Notes)
            .Should().Be(("Note", (string?)null, (double?)null, (string?)null));
    }

    [Fact]
    public async Task DeviceEventWithNotes_KeepsThemOnItselfAlone()
    {
        await UploadAsync(Upload("Site Change", notes: "left arm"));

        await using (var ctx = _db.CreateContext())
            ctx.Notes.IgnoreQueryFilters().Should().BeEmpty();
        (await ReadAsync()).Should().ContainSingle().Which.Notes.Should().Be("left arm");
    }

    private async Task<Guid> NoteIdAsync()
    {
        await using var ctx = _db.CreateContext();
        return ctx.Notes.IgnoreQueryFilters().Single().Id;
    }

    private static long Stamp(IEnumerable<Treatment> treatments) =>
        treatments.Where(t => t.IsValid != false).Should().ContainSingle().Subject.SrvModified!.Value;

    [Fact]
    public async Task NotesEditedAlone_MovesTheTreatmentsStampOnEveryRead()
    {
        var created = await UploadAsync(Upload("Correction Bolus", insulin: 2.0, notes: "before"));
        var searched = Stamp(await ReadAsync());
        await Task.Delay(5);

        await _service.UpdateTreatmentAsync(
            created.Id!, JsonSerializer.Deserialize<Treatment>(Upload("Correction Bolus", insulin: 2.0, notes: "after"))!);

        Stamp(await ReadAsync()).Should().BeGreaterThan(searched);
        (await _service.GetTreatmentByIdAsync(created.Id!))!.SrvModified.Should().BeGreaterThan(searched);
        Stamp(await ReadAsync()).Should().Be(Stamp(await _service.GetTreatmentsModifiedSinceAsync(0)));
    }

    [Fact]
    public async Task NotesCleared_MovesTheTreatmentsStamp()
    {
        var created = await UploadAsync(Upload("Correction Bolus", insulin: 2.0, notes: "before"));
        var searched = Stamp(await ReadAsync());
        await Task.Delay(5);

        await _service.UpdateTreatmentAsync(
            created.Id!, JsonSerializer.Deserialize<Treatment>(Upload("Correction Bolus", insulin: 2.0))!);

        Stamp(await ReadAsync()).Should().BeGreaterThan(searched);
    }

    [Fact]
    public async Task NoteEditedInV4_IsServedOnItsTreatmentWithAMovedStamp()
    {
        await UploadAsync(Upload("Correction Bolus", insulin: 2.0, notes: "left arm"));
        var searched = Stamp(await ReadAsync());
        await Task.Delay(5);

        var note = (await _noteRepo.GetByIdAsync(await NoteIdAsync()))!;
        note.Text = "right arm";
        await _noteRepo.UpdateAsync(note.Id, note, WriteOrigin.Live);

        var served = (await ReadAsync()).Should().ContainSingle().Subject;
        (served.EventType, served.Notes).Should().Be(("Correction Bolus", "right arm"));
        served.SrvModified.Should().BeGreaterThan(searched);
    }

    [Fact]
    public async Task NoteDeletedInV4_LeavesItsTreatmentWithoutNotes()
    {
        await UploadAsync(Upload("Correction Bolus", insulin: 2.0, notes: "after the walk"));

        await _noteRepo.DeleteAsync(await NoteIdAsync(), WriteOrigin.Live);

        var served = (await ReadAsync()).Should().ContainSingle().Subject;
        (served.Insulin, served.Notes).Should().Be((2.0, null));
        (await _store.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RecordDeletedInV4_LeavesItsNoteServedApart_InSearchAndHistory()
    {
        var created = await UploadAsync(Upload("Correction Bolus", insulin: 2.0, notes: "after the walk"));
        var cursor = (await _service.GetTreatmentsModifiedSinceAsync(0)).Max(t => t.SrvModified!.Value);
        await Task.Delay(5);

        await _bolusRepo.DeleteAsync(Guid.Parse(created.Id!), WriteOrigin.Live);

        var noteId = (await NoteIdAsync()).ToString();
        var searched = (await ReadAsync()).Should().ContainSingle().Subject;
        (searched.Id, searched.Notes).Should().Be((noteId, "after the walk"));
        (await _store.CountAsync()).Should().Be(1);

        var history = (await _service.GetTreatmentsModifiedSinceAsync(cursor)).ToList();
        history.Select(t => (t.Id, t.IsValid)).Should().BeEquivalentTo(new[]
        {
            (created.Id, (bool?)false),
            (noteId, (bool?)null),
        });
    }

    [Theory]
    [InlineData("Site Change", "Site Change")]
    [InlineData("Bolus Wizard", "Bolus Wizard")]
    public async Task OtherRecordsWithNotes_ServeThemWithoutANoteApart(string eventType, string served)
    {
        await UploadAsync(Upload(eventType, insulin: eventType == "Bolus Wizard" ? 2.0 : null, notes: "with notes"));

        var read = await ReadAsync();
        read.Should().Contain(t => t.EventType == served)
            .And.OnlyContain(t => t.Notes == "with notes");
        (await _store.CountAsync()).Should().Be(read.Count);
    }

    [Fact]
    public async Task TempBasalWithNotes_ServesThemWithoutANoteApart()
    {
        await UploadAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["eventType"] = "Temp Basal", ["created_at"] = At, ["duration"] = 30, ["absolute"] = 0.5,
            ["notes"] = "exercise",
        }));

        var served = (await ReadAsync()).Should().ContainSingle().Subject;
        (served.EventType, served.Notes).Should().Be(("Temp Basal", "exercise"));
    }

    [Fact]
    public async Task NotesEditedAlone_HistoryResendsTheTreatmentWithTheNewNotes()
    {
        var created = await UploadAsync(Upload("Correction Bolus", insulin: 2.0, notes: "before"));
        var cursor = (await _service.GetTreatmentsModifiedSinceAsync(0)).Max(t => t.SrvModified!.Value);
        await Task.Delay(5);

        await _service.UpdateTreatmentAsync(
            created.Id!, JsonSerializer.Deserialize<Treatment>(Upload("Correction Bolus", insulin: 2.0, notes: "after"))!);

        var resent = (await _service.GetTreatmentsModifiedSinceAsync(cursor))
            .Where(t => t.IsValid != false).Should().ContainSingle().Subject;
        (resent.Id, resent.Notes, resent.IsValid).Should().Be((created.Id, "after", (bool?)null));
    }

    [Fact]
    public async Task NotesCleared_ReadsBackWithoutNotes()
    {
        var created = await UploadAsync(Upload("Correction Bolus", insulin: 2.0, notes: "before"));

        await _service.UpdateTreatmentAsync(
            created.Id!, JsonSerializer.Deserialize<Treatment>(Upload("Correction Bolus", insulin: 2.0))!);

        var served = (await ReadAsync()).Should().ContainSingle().Subject;
        (served.Id, served.Notes).Should().Be((created.Id, null));
        (await _service.GetTreatmentsModifiedSinceAsync(0)).Where(t => t.IsValid != false)
            .Should().ContainSingle().Which.Notes.Should().BeNull();
    }

    [Fact]
    public async Task IdOfTheNoteServedBeforeTheFix_ResolvesToTheTreatment()
    {
        var created = await UploadAsync(Upload("Correction Bolus", insulin: 2.0, notes: "after the walk"));
        Guid noteId;
        await using (var ctx = _db.CreateContext())
            noteId = ctx.Notes.Single().Id;

        var served = await _service.GetTreatmentByIdAsync(MongoObjectId.FromGuid(noteId));

        served.Should().NotBeNull();
        served!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task RecordWithNotes_DeletedById_LeavesNothingServed()
    {
        var created = await UploadAsync(Upload("Meal Bolus", insulin: 3.0, carbs: 30, notes: "lunch"));

        (await _service.DeleteTreatmentAsync(created.Id!)).Should().BeTrue();

        (await ReadAsync()).Should().BeEmpty();
    }

    #endregion

    #region The uploaded event type is served back (#2309)

    [Theory]
    [InlineData("Meal Bolus", 5.0, null)]
    [InlineData("Snack Bolus", 1.0, null)]
    [InlineData("Bolus", 1.0, null)]
    [InlineData("Meal Bolus", null, 30.0)]
    [InlineData("Correction Bolus", 2.0, 15.0)]
    [InlineData("Snack Bolus", 1.0, 15.0)]
    [InlineData("Carb Correction", 1.5, null)]
    [InlineData("Correction Bolus", null, 20.0)]
    public async Task UploadedEventType_IsServedBack(string eventType, double? insulin, double? carbs)
    {
        await UploadAsync(Upload(eventType, insulin, carbs));

        var served = (await ReadAsync()).Should().ContainSingle().Subject;
        (served.EventType, served.Insulin, served.Carbs).Should().Be((eventType, insulin, carbs));
    }

    [Fact]
    public async Task InsulinOnlyMealBolus_IsFoundByItsEventType()
    {
        await UploadAsync(Upload("Correction Bolus", insulin: 2.0));
        await UploadAsync(Upload("Meal Bolus", insulin: 5.0, at: "2026-01-01T11:00:00.000Z"));

        (await ReadAsync("find[eventType]=Meal%20Bolus")).Should().ContainSingle().Which.Insulin.Should().Be(5.0);
        (await ReadAsync("find[eventType]=Correction%20Bolus")).Should().ContainSingle().Which.Insulin.Should().Be(2.0);
    }

    [Fact]
    public async Task InsulinOnlyMealBolus_HistoryServesItsEventType()
    {
        await UploadAsync(Upload("Meal Bolus", insulin: 5.0));

        (await _service.GetTreatmentsModifiedSinceAsync(0)).Should().ContainSingle()
            .Which.EventType.Should().Be("Meal Bolus");
    }

    [Fact]
    public async Task BgCheckWithInsulinAndCarbs_KeepsItsEventTypeOnTheBgCheckAlone()
    {
        await UploadAsync(Upload("BG Check", insulin: 2.0, carbs: 20, glucose: 120));

        (await ReadAsync()).Select(t => t.EventType).Should().BeEquivalentTo("BG Check", "Meal Bolus");
    }

    [Fact]
    public async Task BolusWizard_StillServesItsBolusAsACorrectionBolus()
    {
        await UploadAsync(Upload("Bolus Wizard", insulin: 2.0));

        (await ReadAsync()).Select(t => t.EventType).Should().BeEquivalentTo("Bolus Wizard", "Correction Bolus");
    }

    #endregion
}
