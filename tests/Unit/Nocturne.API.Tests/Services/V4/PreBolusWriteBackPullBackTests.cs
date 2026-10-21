using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
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
/// A treatment with carbs and a preBolus, written back to a Nightscout that splits such documents
/// (<see cref="PreBolusSplittingNightscout"/>) and pulled back by the connector, leaves Nocturne the
/// records it had. The upstream is modelled at 15.0.6, which keeps the <c>_id</c> the pull matches
/// back to the record; from 15.0.7 the copy is found by its <c>identifier</c>, which is #1804's.
/// </summary>
[Trait("Category", "Unit")]
public class PreBolusWriteBackPullBackTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string Key = "6a1f000000000000000000aa";
    private const string BolusTime = "2026-09-30T10:00:00.000Z";
    private static readonly DateTime At = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TestNocturneDbContextFactory _factory;
    private readonly IAuditContext _audit = Mock.Of<IAuditContext>();
    private readonly IDeduplicationService _dedup = Mock.Of<IDeduplicationService>();
    private readonly PreBolusSplittingNightscout _upstream = new(v15_0_8: false);

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

    private static Treatment Treatment(string eventType, double insulin, double carbs, double preBolus) => new()
    {
        Id = Key,
        EventType = eventType,
        CreatedAt = BolusTime,
        Mills = new DateTimeOffset(At).ToUnixTimeMilliseconds(),
        Insulin = insulin,
        Carbs = carbs,
        PreBolus = preBolus,
        DataSource = "careportal",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_meal_bolus_with_a_preBolus_comes_back_as_its_one_carb_intake(bool edited)
    {
        var decomposer = Treatments();
        var treatment = Treatment("Meal Bolus", insulin: 2, carbs: 30, preBolus: 15);
        await decomposer.DecomposeAsync(treatment, WriteOrigin.Live);

        await WriteBackAsync(treatment, edited);
        await PullBackAsync(decomposer);

        var carbs = await _context.CarbIntakes.AsNoTracking().SingleAsync();
        carbs.Carbs.Should().Be(30);
        carbs.Timestamp.Should().Be(At);
        (await _context.Boluses.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_edit_of_a_treatment_an_earlier_write_back_split_comes_back_as_its_one_carb_intake()
    {
        var decomposer = Treatments();
        await decomposer.DecomposeAsync(Treatment("Meal Bolus", insulin: 2, carbs: 30, preBolus: 15), WriteOrigin.Live);
        _upstream.Post(JsonNode.Parse($$"""
            [{"_id":"{{Key}}","identifier":"{{Key}}","eventType":"Meal Bolus","created_at":"{{BolusTime}}","carbs":30,"insulin":2,"preBolus":15}]
            """)!.AsArray());
        _upstream.Documents.Should().HaveCount(2, "the earlier write-back was split");

        var edited = Treatment("Meal Bolus", insulin: 2, carbs: 35, preBolus: 15);
        await decomposer.DecomposeAsync(edited, WriteOrigin.Live);
        await WriteBackAsync(edited, edited: true);
        await PullBackAsync(decomposer);

        var carbs = await _context.CarbIntakes.AsNoTracking().SingleAsync();
        carbs.Carbs.Should().Be(35);
        carbs.Timestamp.Should().Be(At);
    }

    [Fact]
    public async Task A_bolus_wizard_with_a_preBolus_comes_back_with_its_carbs_and_without_its_preBolus()
    {
        var decomposer = Treatments();
        var treatment = Treatment("Bolus Wizard", insulin: 3, carbs: 45, preBolus: 20);
        await decomposer.DecomposeAsync(treatment, WriteOrigin.Live);
        (await _context.BolusCalculations.AsNoTracking().SingleAsync()).PreBolus.Should().Be(20);

        await WriteBackAsync(treatment, edited: true);
        await PullBackAsync(decomposer);

        var calculation = await _context.BolusCalculations.AsNoTracking().SingleAsync();
        calculation.CarbInput.Should().Be(45);
        calculation.PreBolus.Should().BeNull(
            "a pulled copy replaces the calculation, and the copy upstream cannot carry preBolus beside its carbs");
        (await _context.CarbIntakes.AsNoTracking().SingleAsync()).Carbs.Should().Be(45);
        (await _context.Boluses.AsNoTracking().CountAsync()).Should().Be(1);
    }

    private async Task WriteBackAsync(Treatment treatment, bool edited)
    {
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
            new HttpClient(_upstream),
            loader.Object,
            new NightscoutCircuitBreaker(new FakeTimeProvider()),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);

        if (edited)
            await sink.OnUpdatedAsync(treatment);
        else
            await sink.OnCreatedAsync([treatment]);
    }

    private async Task PullBackAsync(TreatmentDecomposer decomposer)
    {
        var pulled = JsonSerializer.Deserialize<Treatment[]>(_upstream.Served(), JsonDefaults.CaseInsensitive)!;
        foreach (var treatment in pulled)
            treatment.DataSource = DataSources.NightscoutConnector;
        await decomposer.DecomposeBatchAsync(pulled, WriteOrigin.Live);
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
