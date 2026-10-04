using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// The treatment storage events as <see cref="JsonHubProtocol"/> writes them, from a v1 upload through
/// <see cref="TreatmentService"/>, the <see cref="TreatmentReadService"/> store and the real decomposer
/// and repositories, compared with the <c>_id</c> the v1 read serves for the same record.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentSocketIdentityTests : IDisposable
{
    private static readonly Guid TenantId = MockTenantAccessor.DefaultTenantId;

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TreatmentService _service;
    private readonly JsonHubProtocolOptions _protocol = new();
    private readonly List<(string Method, object Payload)> _sent = [];
    private readonly UpstreamStore _upstream = new();
    private readonly BolusRepository _bolusRepo;

    public TreatmentSocketIdentityTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();

        IAuditContext caller = new AuditContext
        {
            SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
            SubjectName = "uploader",
            AuthType = "ApiSecret",
            Endpoint = "POST /api/v1/treatments",
        };

        var dedup = new Mock<IDeduplicationService>().Object;
        var contexts = new TestTenantDbContextFactory(_context);
        var bolusRepo = _bolusRepo = new BolusRepository(contexts, dedup, caller, NullLogger<BolusRepository>.Instance);
        var carbRepo = new CarbIntakeRepository(contexts, dedup, caller, NullLogger<CarbIntakeRepository>.Instance);
        var bgCheckRepo = new BGCheckRepository(contexts, dedup, caller, NullLogger<BGCheckRepository>.Instance);
        var noteRepo = new NoteRepository(contexts, dedup, caller, NullLogger<NoteRepository>.Instance);
        var deviceEventRepo = new DeviceEventRepository(contexts, dedup, caller, NullLogger<DeviceEventRepository>.Instance);
        var bolusCalcRepo = new BolusCalculationRepository(contexts, dedup, caller, NullLogger<BolusCalculationRepository>.Instance);

        var tempBasalRepo = new TempBasalRepository(contexts, dedup, caller, NullLogger<TempBasalRepository>.Instance);

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
            _context,
            bolusRepo, tempBasalRepo,
            carbRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
            Mock.Of<IStateSpanService>(),
            foods.Object,
            devices.Object,
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IProfileDecomposer>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            caller,
            dedup,
            NullLogger<TreatmentDecomposer>.Instance);

        var projection = new V4ToLegacyProjectionService(
            Mock.Of<ISensorGlucoseRepository>(),
            bolusRepo, carbRepo, bgCheckRepo, noteRepo, deviceEventRepo,
            tempBasalRepo, bolusCalcRepo,
            foods.Object,
            _context,
            NullLogger<V4ToLegacyProjectionService>.Instance);

        var pipeline = new DecompositionPipeline(
            new ServiceCollection().AddSingleton<IDecomposer<Treatment>>(decomposer).BuildServiceProvider(),
            NullLogger<DecompositionPipeline>.Instance);

        var store = new TreatmentReadService(
            projection, decomposer, pipeline,
            tempBasalRepo, bolusRepo, carbRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
            NullLogger<TreatmentReadService>.Instance);

        var broadcast = new SignalRBroadcastService(
            StubHub<DataHub>(),
            StubHub<AlarmHub>(),
            StubHub<ConfigHub>(),
            StubHub<AlertHub>(),
            StubHub<HomeAssistantHub>(),
            StubHub<OverviewHub>(),
            MockTenantAccessor.Create().Object,
            Options.Create(_protocol),
            NullLogger<SignalRBroadcastService>.Instance);

        _service = new TreatmentService(
            store, decomposer, Mock.Of<ITreatmentCache>(),
            new CompositeDataEventSink<Treatment>(
            [
                new SignalRTreatmentEventSink(broadcast, NullLogger<SignalRTreatmentEventSink>.Instance),
                new NightscoutTreatmentWriteBackSink(
                    new HttpClient(_upstream),
                    Mock.Of<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>(l =>
                        l.LoadForTenantAsync(It.IsAny<CancellationToken>()) == Task.FromResult(new NightscoutConnectorConfiguration
                        {
                            Url = "https://nightscout.example.com",
                            ApiSecret = "synthetic-secret",
                            WriteBackEnabled = true,
                        })),
                    new NightscoutCircuitBreaker(),
                    NullLogger<NightscoutTreatmentWriteBackSink>.Instance),
            ]),
            Mock.Of<IPatientInsulinRepository>(), NullLogger<TreatmentService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private IHubContext<THub> StubHub<THub>()
        where THub : Hub
    {
        var proxy = new Mock<IClientProxy>();
        proxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((method, args, _) => _sent.Add((method, args[0]!)))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);

        var hub = new Mock<IHubContext<THub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }

    /// <summary>The payloads of every <paramref name="method"/> event, as a client receives them.</summary>
    private List<JsonElement> Events(string method) =>
        _sent
            .Where(s => s.Method == method)
            .Select(s =>
            {
                var frame = new JsonHubProtocol(Options.Create(_protocol))
                    .GetMessageBytes(new InvocationMessage(method, [s.Payload]))
                    .ToArray();
                return JsonSerializer
                    .Deserialize<JsonElement>(frame.AsSpan(0, frame.Length - 1)) // trailing 0x1e separator
                    .GetProperty("arguments")[0];
            })
            .ToList();

    private static string? Id(JsonElement payload) => payload.GetProperty("doc").GetProperty("_id").GetString();

    private static Treatment Upload(string json) => JsonSerializer.Deserialize<Treatment>(json)!;

    /// <summary>The <c>_id</c> the v1 read serves for the only treatment stored.</summary>
    private async Task<string> RestIdAsync()
    {
        var served = (await _service.GetTreatmentsAsync(count: 10)).Should().ContainSingle().Subject;
        return JsonSerializer.SerializeToElement(served).GetProperty("_id").GetString()!;
    }

    private const string Note = """{"eventType":"Note","notes":"socket id","created_at":"2026-01-01T12:00:00.000Z","enteredBy":"e2e"}""";

    private const string LoopBolus =
        """{"eventType":"Correction Bolus","insulin":0.65,"created_at":"2026-01-01T12:30:00.000Z","enteredBy":"loop://iphone","syncIdentifier":"4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f"}""";

    private const string ObjectIdCarbs =
        """{"_id":"65a1b2c3d4e5f60718293a4b","eventType":"Carb Correction","carbs":12,"created_at":"2026-01-01T13:00:00.000Z"}""";

    [Theory]
    [InlineData(Note)]
    [InlineData(LoopBolus)]
    [InlineData(ObjectIdCarbs)]
    public async Task Create_CarriesTheIdTheReadServes(string upload)
    {
        var created = await _service.CreateTreatmentsAsync([Upload(upload)]);

        var restId = await RestIdAsync();
        var create = Events("create").Should().ContainSingle().Subject;
        Id(create).Should().Be(restId);
        create.GetProperty("doc").GetProperty("identifier").GetString().Should().Be(restId);
        JsonSerializer.SerializeToElement(created.Single()).GetProperty("_id").GetString()
            .Should().Be(restId, "the create response names the record by the id the read serves too");
    }

    [Fact]
    public async Task UpdatePatchAndDelete_CarryTheIdTheReadServes()
    {
        await _service.CreateTreatmentsAsync([Upload(LoopBolus)]);
        var restId = await RestIdAsync();

        var replacement = Upload(LoopBolus);
        replacement.Insulin = 0.8;
        await _service.UpdateTreatmentAsync(restId, replacement);
        await _service.PatchTreatmentAsync(restId, JsonSerializer.Deserialize<JsonElement>("""{"insulin":0.9}"""));
        (await RestIdAsync()).Should().Be(restId);
        (await _service.DeleteTreatmentAsync(restId)).Should().BeTrue();

        Events("update").Select(Id).Should().Equal(restId, restId);
        Events("delete").Should().ContainSingle().Which.GetProperty("identifier").GetString().Should().Be(restId);
    }

    private const string TempBasal =
        """{"eventType":"Temp Basal","duration":30,"absolute":0.4,"rate":0.4,"created_at":"2026-01-01T14:00:00.000Z","enteredBy":"e2e"}""";

    private const string ObjectIdTempBasal =
        """{"_id":"65a1b2c3d4e5f60718293a5c","eventType":"Temp Basal","duration":30,"absolute":0.6,"rate":0.6,"created_at":"2026-01-01T14:30:00.000Z"}""";

    /// <summary>The one treatment document the last write-back sent: a POST's array element or a PUT's body.</summary>
    private JsonElement LastSent()
    {
        var body = JsonSerializer.Deserialize<JsonElement>(_upstream.Requests.Last(r => r.Method != HttpMethod.Get).Body);
        if (body.ValueKind != JsonValueKind.Array)
            return body;
        body.GetArrayLength().Should().Be(1);
        return body[0];
    }

    /// <summary>
    /// A create's upstream identity (<see cref="UpstreamIdentityJson.TreatmentWireKey"/>): <c>_id</c> and
    /// <c>identifier</c> are both the legacy key coerced to an ObjectId.
    /// </summary>
    private static void ShouldCarryTheWireKey(JsonElement sent, string legacyKey)
    {
        var wire = MongoObjectId.Coerce(legacyKey);
        sent.GetProperty("_id").GetString().Should().MatchRegex("^[0-9a-f]{24}$").And.Be(wire);
        sent.GetProperty("identifier").GetString().Should().Be(wire);
    }

    /// <summary>The uuid of the stored record the only treatment is served from.</summary>
    private async Task<Guid> RecordIdAsync()
    {
        var served = (await _service.GetTreatmentsAsync(count: 10)).Should().ContainSingle().Subject;
        served.RecordId.Should().NotBeNull();
        return served.RecordId!.Value;
    }

    /// <summary>
    /// The identifier lookup an edit sends first: every form a write-back may have left the copy under
    /// (the coerced key, the record's uuid prefix, the raw key, the record's full uuid; without repeats,
    /// in that order), with the <c>created_at</c> bound that lifts Nightscout's four-day default.
    /// </summary>
    private static string IdentifierLookup(string legacyKey, Guid recordId)
    {
        var forms = new[] { MongoObjectId.Coerce(legacyKey)!, MongoObjectId.FromGuid(recordId), legacyKey, recordId.ToString() }
            .Distinct()
            .ToList();
        var ins = string.Concat(forms.Select((form, i) => $"find[identifier][$in][{i}]={Uri.EscapeDataString(form)}&"));
        return $"/api/v1/treatments.json?{ins}find[created_at][$gte]=1970-01-01T00%3A00%3A00.000Z&count=10";
    }

    private static string IdLookup(string legacyKey) =>
        $"/api/v1/treatments.json?find[_id]={MongoObjectId.Coerce(legacyKey)}&count=1";

    /// <summary>
    /// The reads an edit held under no identifier sends after <see cref="IdentifierLookup"/>:
    /// <c>find[_id]</c> = the coerced key, then, for a legacy id that is not an ObjectId and nothing
    /// held under that <c>_id</c>, <c>find[_id][$in]</c> = the raw legacy id as a string.
    /// </summary>
    private static List<string> IdLookups(string legacyKey, bool heldUnderId) =>
        heldUnderId || MongoObjectId.IsObjectId(legacyKey)
            ? [IdLookup(legacyKey)]
            : [IdLookup(legacyKey), $"/api/v1/treatments.json?find[_id][$in][0]={Uri.EscapeDataString(legacyKey)}&count=1"];

    /// <summary>
    /// An edit of a treatment whose create left its copy upstream: one identifier lookup, which finds the
    /// copy under the ObjectId the upstream minted, then a PUT under that ObjectId with the identifier
    /// it was found under. The upstream still holds one copy.
    /// </summary>
    private void ShouldHaveFoundTheCopyAndPutOntoIt(string legacyKey, Guid recordId)
    {
        var wire = MongoObjectId.Coerce(legacyKey)!;
        var copy = _upstream.Held.Should().ContainSingle().Subject;
        var edit = _upstream.Requests.TakeLast(2).ToList();
        edit.Select(r => r.Method).Should().Equal(HttpMethod.Get, HttpMethod.Put);
        edit[0].PathAndQuery.Should().Be(IdentifierLookup(legacyKey, recordId));
        var sent = LastSent();
        sent.GetProperty("_id").GetString().Should().Be(copy.Id).And.NotBe(wire);
        sent.GetProperty("identifier").GetString().Should().Be(wire);
        copy.Identifier.Should().Be(wire);
    }

    [Theory]
    [InlineData(Note, null)]
    [InlineData(LoopBolus, "4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f")]
    [InlineData(ObjectIdCarbs, "65a1b2c3d4e5f60718293a4b")]
    [InlineData(TempBasal, null)]
    [InlineData(ObjectIdTempBasal, "65a1b2c3d4e5f60718293a5c")]
    public async Task WriteBack_OfACreate_SendsTheWireKey(string upload, string? uploadedKey)
    {
        var submitted = Upload(upload);
        await _service.CreateTreatmentsAsync([submitted]);

        _upstream.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Post);
        var legacyKey = submitted.Id!;
        if (uploadedKey is null)
            legacyKey.Should().StartWith(TreatmentClientId.SyntheticIdPrefix);
        else
            legacyKey.Should().Be(uploadedKey);
        ShouldCarryTheWireKey(LastSent(), legacyKey);
    }

    [Theory]
    [InlineData(Note)]
    [InlineData(LoopBolus)]
    [InlineData(ObjectIdCarbs)]
    [InlineData(TempBasal)]
    [InlineData(ObjectIdTempBasal)]
    public async Task WriteBack_OfAPut_LandsOnTheCreatesCopy(string upload)
    {
        var submitted = Upload(upload);
        await _service.CreateTreatmentsAsync([submitted]);
        var restId = await RestIdAsync();
        var recordId = await RecordIdAsync();

        var replacement = Upload(upload);
        replacement.EnteredBy = "e2e-put";
        (await _service.UpdateTreatmentAsync(restId, replacement)).Should().NotBeNull();

        _upstream.Requests.Should().HaveCount(3);
        ShouldHaveFoundTheCopyAndPutOntoIt(submitted.Id!, recordId);
    }

    /// <summary>
    /// A PATCH by the served id and one by the raw legacy key (what an uploader that kept its own id
    /// patches by) both answer and broadcast the served id, and look the copy up under the create's
    /// forms.
    /// </summary>
    [Theory]
    [InlineData(Note, false)]
    [InlineData(Note, true)]
    [InlineData(LoopBolus, false)]
    [InlineData(LoopBolus, true)]
    [InlineData(ObjectIdCarbs, false)]
    [InlineData(ObjectIdCarbs, true)]
    [InlineData(TempBasal, false)]
    [InlineData(TempBasal, true)]
    [InlineData(ObjectIdTempBasal, false)]
    [InlineData(ObjectIdTempBasal, true)]
    public async Task WriteBack_OfAPatch_LandsOnTheCreatesCopy(string upload, bool byLegacyKey)
    {
        var submitted = Upload(upload);
        await _service.CreateTreatmentsAsync([submitted]);
        var restId = await RestIdAsync();
        var recordId = await RecordIdAsync();

        var patched = await _service.PatchTreatmentAsync(
            byLegacyKey ? submitted.Id! : restId, JsonSerializer.Deserialize<JsonElement>("""{"enteredBy":"e2e-patch"}"""));

        patched.Should().NotBeNull();
        JsonSerializer.SerializeToElement(patched).GetProperty("_id").GetString().Should().Be(restId);
        Events("update").Select(Id).Should().Equal(restId);
        _upstream.Requests.Should().HaveCount(3);
        ShouldHaveFoundTheCopyAndPutOntoIt(submitted.Id!, recordId);
    }

    /// <summary>
    /// An edit of a treatment no identifier names upstream reads by <c>_id</c> = the coerced key next:
    /// the original a Nightscout migration imported is PUT under it, keeping the identifier it holds
    /// (none here); a treatment held nowhere is looked for under its raw legacy id as a string
    /// <c>_id</c> when that is not an ObjectId, then POSTed as a create, under both keys.
    /// </summary>
    [Theory]
    [InlineData(Note, false)]
    [InlineData(Note, true)]
    [InlineData(LoopBolus, false)]
    [InlineData(LoopBolus, true)]
    [InlineData(ObjectIdCarbs, false)]
    [InlineData(ObjectIdCarbs, true)]
    [InlineData(TempBasal, false)]
    [InlineData(TempBasal, true)]
    [InlineData(ObjectIdTempBasal, false)]
    [InlineData(ObjectIdTempBasal, true)]
    public async Task WriteBack_OfAnEdit_NotHeldUnderAnIdentifier_ReadsByIdThenWrites(string upload, bool heldUnderId)
    {
        var submitted = Upload(upload);
        await _service.CreateTreatmentsAsync([submitted]);
        var restId = await RestIdAsync();
        var recordId = await RecordIdAsync();
        var legacyKey = submitted.Id!;
        var wire = MongoObjectId.Coerce(legacyKey)!;
        _upstream.Held.Clear();
        if (heldUnderId)
            _upstream.Held.Add(new UpstreamStore.Copy(wire, null, "{}"));
        _upstream.Requests.Clear();

        await _service.PatchTreatmentAsync(restId, JsonSerializer.Deserialize<JsonElement>("""{"enteredBy":"e2e-patch"}"""));

        var reads = IdLookups(legacyKey, heldUnderId).Prepend(IdentifierLookup(legacyKey, recordId)).ToList();
        _upstream.Requests.SkipLast(1).Select(r => (r.Method, r.PathAndQuery)).Should().Equal(
            reads.Select(read => (HttpMethod.Get, read)));
        _upstream.Requests[^1].Method.Should().Be(heldUnderId ? HttpMethod.Put : HttpMethod.Post);
        var sent = LastSent();
        sent.GetProperty("_id").GetString().Should().Be(wire);
        if (heldUnderId)
            sent.TryGetProperty("identifier", out _).Should().BeFalse("the original holds no identifier to keep");
        else
            sent.GetProperty("identifier").GetString().Should().Be(wire);
        _upstream.Held.Should().ContainSingle();
    }

    public enum Write { Create, Put, Patch }

    /// <summary>
    /// The copy of each write-back comes back onto the stored treatment, whether Nightscout keeps the
    /// identifier as its <c>_id</c> (15.0.6 and earlier) or holds it under an ObjectId it minted
    /// (15.0.7 and later, as <see cref="UpstreamStore"/> and
    /// <c>e2e/mocks/vendors/nightscout-writeback.ts</c> do).
    /// </summary>
    /// <remarks>
    /// A copy of a treatment whose legacy key is not an ObjectId comes back under that key's hash or
    /// uuid prefix, which only the PostgreSQL resolvers answer; <c>WriteBackEchoIntegrationTests</c>
    /// covers those keys.
    /// </remarks>
    public static TheoryData<string, Write, bool> PullBacks()
    {
        var data = new TheoryData<string, Write, bool>();
        foreach (var upload in new[] { ObjectIdCarbs, ObjectIdTempBasal })
            foreach (var write in Enum.GetValues<Write>())
                foreach (var reMinted in new[] { false, true })
                    data.Add(upload, write, reMinted);
        return data;
    }

    [Theory]
    [MemberData(nameof(PullBacks))]
    public async Task PullBack_OfAWriteBack_UpdatesTheStoredTreatment(string upload, Write write, bool reMinted)
    {
        await _service.CreateTreatmentsAsync([Upload(upload)]);
        var restId = await RestIdAsync();

        switch (write)
        {
            case Write.Put:
                var replacement = Upload(upload);
                replacement.EnteredBy = "e2e-put";
                await _service.UpdateTreatmentAsync(restId, replacement);
                break;
            case Write.Patch:
                await _service.PatchTreatmentAsync(restId, JsonSerializer.Deserialize<JsonElement>("""{"enteredBy":"e2e-patch"}"""));
                break;
        }

        var held = _upstream.Held.Should().ContainSingle().Subject;
        var pulled = JsonSerializer.Deserialize<Treatment>(held.Body)!;
        pulled.UpstreamIdentifier = held.Identifier;
        pulled.Id = reMinted ? held.Id : held.Identifier;
        pulled.DataSource = DataSources.NightscoutConnector;
        var before = (await _service.GetTreatmentsAsync(count: 10)).Single();
        await _service.CreateTreatmentsAsync([pulled]);

        (await RestIdAsync()).Should().Be(restId);
        var after = (await _service.GetTreatmentsAsync(count: 10)).Single();
        after.DataSource.Should().Be(before.DataSource, "a write-back echo leaves the record it names as it stands");
        after.EnteredBy.Should().Be(before.EnteredBy);
    }

    /// <summary>
    /// A v4-native treatment has no legacy id. A v1 PATCH re-decomposes it under the id it is served by,
    /// so the legacy id it takes names the same record, and the read and the write-back agree afterwards.
    /// Nothing upstream holds it, so the edit goes out as the create would.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Patch_OfANativeTreatment_TakesALegacyIdTheServedIdStillNames(bool byUuid)
    {
        var bolus = await _bolusRepo.CreateAsync(
            new V4Models.Bolus { Timestamp = new DateTime(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc), Insulin = 1.5 },
            WriteOrigin.Live);
        var restId = await RestIdAsync();
        restId.Should().Be(MongoObjectId.FromGuid(bolus.Id));

        var patched = await _service.PatchTreatmentAsync(
            byUuid ? bolus.Id.ToString() : restId, JsonSerializer.Deserialize<JsonElement>("""{"insulin":1.75}"""));

        JsonSerializer.SerializeToElement(patched).GetProperty("_id").GetString().Should().Be(restId);
        (await RestIdAsync()).Should().Be(restId);
        var stored = (await _bolusRepo.GetByIdAsync(bolus.Id))!;
        stored.Insulin.Should().Be(1.75);
        stored.LegacyId.Should().BeOneOf(restId, bolus.Id.ToString());
        _upstream.Requests.Select(r => r.PathAndQuery).Should().Equal(
            IdLookups(stored.LegacyId!, heldUnderId: false)
                .Prepend(IdentifierLookup(stored.LegacyId!, bolus.Id))
                .Append("/api/v1/treatments"));
        _upstream.Requests[^1].Method.Should().Be(HttpMethod.Post);
        ShouldCarryTheWireKey(LastSent(), stored.LegacyId!);
    }

    /// <summary>
    /// The treatments of a Nightscout 15.0.7 or later, as far as write-back reaches them: a document
    /// carrying an <c>identifier</c> is upserted by it and its <c>_id</c> is dropped, so a new copy is
    /// held under a minted ObjectId; one without is replaced by its <c>_id</c>.
    /// <c>find[identifier][$in]</c>, <c>find[_id]</c> and <c>find[_id][$in]</c> read them back.
    /// </summary>
    private sealed class UpstreamStore : HttpMessageHandler
    {
        public sealed record Copy(string Id, string? Identifier, string Body);

        public List<(HttpMethod Method, string PathAndQuery, string Body)> Requests { get; } = [];

        public List<Copy> Held { get; } = [];

        private int _minted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));

            if (request.Method == HttpMethod.Get)
                return Answer(JsonSerializer.Serialize(Find(request.RequestUri.Query)
                    .Select(c => c.Identifier is null
                        ? new Dictionary<string, string> { ["_id"] = c.Id }
                        : new Dictionary<string, string> { ["_id"] = c.Id, ["identifier"] = c.Identifier })));

            var sent = JsonSerializer.Deserialize<JsonElement>(body);
            foreach (var doc in sent.ValueKind == JsonValueKind.Array ? sent.EnumerateArray().ToList() : [sent])
                Upsert(doc);
            return Answer(body);
        }

        private static HttpResponseMessage Answer(string json) =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };

        private IEnumerable<Copy> Find(string query)
        {
            var terms = query.TrimStart('?').Split('&')
                .Select(term => term.Split('=', 2))
                .Select(kv => (Key: Uri.UnescapeDataString(kv[0]), Value: Uri.UnescapeDataString(kv[1])))
                .ToList();
            var ids = terms
                .Where(t => t.Key == "find[_id]" || t.Key.StartsWith("find[_id][$in]", StringComparison.Ordinal))
                .Select(t => t.Value)
                .ToHashSet();
            if (ids.Count > 0)
                return Held.Where(c => ids.Contains(c.Id));

            var identifiers = terms
                .Where(t => t.Key.StartsWith("find[identifier][$in]", StringComparison.Ordinal))
                .Select(t => t.Value)
                .ToHashSet();
            return Held.Where(c => c.Identifier is not null && identifiers.Contains(c.Identifier));
        }

        private void Upsert(JsonElement doc)
        {
            var identifier = doc.TryGetProperty("identifier", out var i) ? i.GetString() : null;
            var id = doc.TryGetProperty("_id", out var d) ? d.GetString() : null;
            var existing = identifier is not null
                ? Held.FindIndex(c => c.Identifier == identifier)
                : Held.FindIndex(c => c.Id == id);
            var heldId = existing >= 0
                ? Held[existing].Id
                : identifier is not null || id is null ? $"65f0e2e0{++_minted:x16}" : id;
            var copy = new Copy(heldId, identifier, doc.GetRawText());
            if (existing >= 0)
                Held[existing] = copy;
            else
                Held.Add(copy);
        }
    }

    /// <summary>
    /// A re-upload of a stored treatment updates it, so it is announced as the update it is, under the
    /// served id, and written back as an edit: the lookup finds the create's copy and the re-upload is
    /// PUT onto it, where an unprobed create would leave a second copy upstream.
    /// </summary>
    [Theory]
    [InlineData(Note)]
    [InlineData(LoopBolus)]
    [InlineData(ObjectIdCarbs)]
    [InlineData(TempBasal)]
    [InlineData(ObjectIdTempBasal)]
    public async Task ReUpload_OfAStoredTreatment_IsAnnouncedAndWrittenBackAsAnUpdate(string upload)
    {
        var submitted = Upload(upload);
        await _service.CreateTreatmentsAsync([submitted]);
        var restId = await RestIdAsync();
        var recordId = await RecordIdAsync();

        var resent = Upload(upload);
        resent.Id = submitted.Id;
        resent.EnteredBy = "e2e-resend";
        var reUpload = await _service.CreateTreatmentsAsync([resent]);

        JsonSerializer.SerializeToElement(reUpload.Should().ContainSingle().Subject)
            .GetProperty("_id").GetString().Should().Be(restId);
        (await RestIdAsync()).Should().Be(restId);
        Events("create").Should().ContainSingle("only the first upload inserted");
        Events("update").Select(Id).Should().Equal(restId);
        _upstream.Requests.Should().HaveCount(3);
        ShouldHaveFoundTheCopyAndPutOntoIt(submitted.Id!, recordId);
    }

    [Fact]
    public async Task ReUpload_OfATreatmentTheUserDeleted_BroadcastsNoCreate()
    {
        await _service.CreateTreatmentsAsync([Upload(LoopBolus)]);
        var restId = await RestIdAsync();
        (await _service.DeleteTreatmentAsync(restId)).Should().BeTrue();

        var reUpload = await _service.CreateTreatmentsAsync([Upload(LoopBolus)]);

        reUpload.Should().BeEmpty();
        reUpload.SkippedDeleted.Should().Be(1);
        JsonSerializer.SerializeToElement(reUpload.Settled.Should().ContainSingle().Subject)
            .GetProperty("_id").GetString().Should().Be(restId, "the reply still names the deleted treatment as it was served");
        (await _service.GetTreatmentsAsync(count: 10)).Should().BeEmpty();
        Events("create").Should().ContainSingle("only the first upload was written");
    }
}
