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
    private readonly UpstreamCapture _upstream = new();

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
        var bolusRepo = new BolusRepository(contexts, dedup, caller, NullLogger<BolusRepository>.Instance);
        var carbRepo = new CarbIntakeRepository(contexts, dedup, caller, NullLogger<CarbIntakeRepository>.Instance);
        var bgCheckRepo = new BGCheckRepository(contexts, dedup, caller, NullLogger<BGCheckRepository>.Instance);
        var noteRepo = new NoteRepository(contexts, dedup, caller, NullLogger<NoteRepository>.Instance);
        var deviceEventRepo = new DeviceEventRepository(contexts, dedup, caller, NullLogger<DeviceEventRepository>.Instance);
        var bolusCalcRepo = new BolusCalculationRepository(contexts, dedup, caller, NullLogger<BolusCalculationRepository>.Instance);

        var tempBasalRepo = new Mock<ITempBasalRepository>();
        tempBasalRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

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
            bolusRepo, tempBasalRepo.Object,
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
            tempBasalRepo.Object, bolusCalcRepo,
            foods.Object,
            _context,
            NullLogger<V4ToLegacyProjectionService>.Instance);

        var pipeline = new DecompositionPipeline(
            new ServiceCollection().AddSingleton<IDecomposer<Treatment>>(decomposer).BuildServiceProvider(),
            NullLogger<DecompositionPipeline>.Instance);

        var store = new TreatmentReadService(
            projection, decomposer, pipeline,
            tempBasalRepo.Object, bolusRepo, carbRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
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

    /// <summary>
    /// The write-back keys a treatment by its legacy id, as before the served id changed, not by the
    /// id the reads serve it under.
    /// </summary>
    [Theory]
    [InlineData(Note)]
    [InlineData(LoopBolus)]
    [InlineData(ObjectIdCarbs)]
    public async Task WriteBack_SendsTheLegacyIdCoerced(string upload)
    {
        var submitted = Upload(upload);
        await _service.CreateTreatmentsAsync([submitted]);
        var restId = await RestIdAsync();

        var sent = JsonSerializer.Deserialize<JsonElement>(_upstream.Bodies.Should().ContainSingle().Subject)[0];

        sent.GetProperty("_id").GetString().Should().Be(MongoObjectId.Coerce(submitted.Id)).And.NotBe(restId);
    }

    /// <summary>
    /// A treatment uploaded under its own ObjectId goes upstream under it, so the connector's pull of
    /// the copy updates the stored treatment instead of storing a second one.
    /// </summary>
    [Fact]
    public async Task PullBack_OfATreatmentUploadedUnderAnObjectId_UpdatesTheStoredOne()
    {
        await _service.CreateTreatmentsAsync([Upload(ObjectIdCarbs)]);
        var restId = await RestIdAsync();

        var pulled = JsonSerializer.Deserialize<List<Treatment>>(_upstream.Bodies.Should().ContainSingle().Subject)!;
        pulled.ForEach(t => t.DataSource = DataSources.NightscoutConnector);
        await _service.CreateTreatmentsAsync(pulled);

        (await RestIdAsync()).Should().Be(restId);
    }

    private sealed class UpstreamCapture : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
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
