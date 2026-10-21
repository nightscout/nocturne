using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// The document the Nightscout write-back sends for a treatment with carbs and a preBolus, pulled
/// back by the connector, lands on the records it came from: Nightscout splits a document carrying a
/// preBolus, so the write-back leaves it out.
/// </summary>
[Trait("Category", "Unit")]
public class PreBolusWriteBackPullBackTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const long At = 1_790_762_400_000;

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TestNocturneDbContextFactory _factory;
    private readonly IAuditContext _audit = Mock.Of<IAuditContext>();
    private readonly IDeduplicationService _dedup = Mock.Of<IDeduplicationService>();

    public PreBolusWriteBackPullBackTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
        _factory = new TestNocturneDbContextFactory(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pulled_back_meal_bolus_with_a_preBolus_leaves_one_carb_intake(bool edited)
    {
        var decomposer = Treatments();
        var treatment = new Treatment
        {
            Id = "6a1f000000000000000000aa",
            EventType = "Meal Bolus",
            Mills = At,
            CreatedAt = "2026-09-30T10:00:00.000Z",
            Insulin = 2,
            Carbs = 30,
            PreBolus = 15,
            DataSource = "careportal",
        };
        await decomposer.DecomposeAsync(treatment, WriteOrigin.Live);

        var sent = await WrittenBackAsync(treatment, edited);
        sent.Should().ContainSingle();
        var pulled = sent[0];
        pulled.DataSource = DataSources.NightscoutConnector;
        await decomposer.DecomposeBatchAsync([pulled], WriteOrigin.Live);

        var carbs = await _context.CarbIntakes.AsNoTracking().SingleAsync();
        carbs.Carbs.Should().Be(30);
        carbs.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(At).UtcDateTime);
        (await _context.Boluses.AsNoTracking().CountAsync()).Should().Be(1);
    }

    private static async Task<Treatment[]> WrittenBackAsync(Treatment treatment, bool edited)
    {
        var upstream = new CapturingHandler();
        var config = new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret-12345",
            WriteBackEnabled = true,
            WriteBackBatchSize = 50,
        };
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(config);
        var sink = new NightscoutTreatmentWriteBackSink(
            new HttpClient(upstream),
            loader.Object,
            new NightscoutCircuitBreaker(new FakeTimeProvider()),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);

        if (edited)
            await sink.OnUpdatedAsync(treatment);
        else
            await sink.OnCreatedAsync([treatment]);

        upstream.Body.Should().NotBeNull();
        upstream.Body.Should().NotContain("preBolus");
        using var document = JsonDocument.Parse(upstream.Body!);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.Deserialize<Treatment[]>()!
            : [document.RootElement.Deserialize<Treatment>()!];
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private TreatmentDecomposer Treatments()
    {
        var foods = new Mock<ITreatmentFoodService>();
        foods
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return new TreatmentDecomposer(
            _factory,
            new BolusRepository(_factory, _dedup, _audit, NullLogger<BolusRepository>.Instance),
            new TempBasalRepository(_factory, _dedup, _audit, NullLogger<TempBasalRepository>.Instance),
            new CarbIntakeRepository(_factory, _dedup, _audit, NullLogger<CarbIntakeRepository>.Instance),
            new BGCheckRepository(_factory, _dedup, _audit, NullLogger<BGCheckRepository>.Instance),
            new NoteRepository(_factory, _dedup, _audit, NullLogger<NoteRepository>.Instance),
            new DeviceEventRepository(_factory, _dedup, _audit, NullLogger<DeviceEventRepository>.Instance),
            new BolusCalculationRepository(_factory, _dedup, _audit, NullLogger<BolusCalculationRepository>.Instance),
            Mock.Of<IStateSpanService>(),
            ProfileActivationTestDeps.Repository(_context),
            ProfileActivationTestDeps.Writer(_context),
            foods.Object,
            Mock.Of<IDeviceService>(),
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IProfileDecomposer>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            _audit,
            NullLogger<TreatmentDecomposer>.Instance);
    }
}
