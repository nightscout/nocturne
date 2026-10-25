using Nocturne.API.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Events;
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

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// Deleting a legacy treatment's rows bypasses the device-event repository, so the decomposer runs
/// the <see cref="IDeviceEventReactor"/> delete reaction itself: a deleted careportal or uploader
/// "Sensor Change" must undo the tracker run it started, as a V4 delete does.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentDecomposerDeviceEventReactionTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const long At = 1_780_000_000_000;
    private const string LegacyId = "6500000000000000000000s1";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly List<V4Models.DeviceEvent> _deleted = [];
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerDeviceEventReactionTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, SqliteNpgsqlJson.Translate);
        _context = _db.CreateContext();
        var factory = new TestNocturneDbContextFactory(_context);
        var audit = Mock.Of<IAuditContext>();
        var dedup = Mock.Of<IDeduplicationService>();

        var reactor = new Mock<IDeviceEventReactor>();
        reactor
            .Setup(r => r.OnDeletedAsync(It.IsAny<IReadOnlyList<V4Models.DeviceEvent>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<V4Models.DeviceEvent> deleted, CancellationToken _) => _deleted.AddRange(deleted))
            .Returns(Task.CompletedTask);

        _decomposer = new TreatmentDecomposer(
            _context.AsFactory(),
            new BolusRepository(factory, dedup, audit, NullLogger<BolusRepository>.Instance),
            new TempBasalRepository(factory, dedup, audit, NullLogger<TempBasalRepository>.Instance),
            new CarbIntakeRepository(factory, dedup, audit, NullLogger<CarbIntakeRepository>.Instance),
            new BGCheckRepository(factory, dedup, audit, NullLogger<BGCheckRepository>.Instance),
            new NoteRepository(factory, dedup, audit, NullLogger<NoteRepository>.Instance),
            new DeviceEventRepository(factory, dedup, audit, NullLogger<DeviceEventRepository>.Instance),
            new BolusCalculationRepository(factory, dedup, audit, NullLogger<BolusCalculationRepository>.Instance),
            Mock.Of<IStateSpanService>(),
            ProfileActivationTestDeps.Repository(_context),
            ProfileActivationTestDeps.Writer(_context),
            Mock.Of<ITreatmentFoodService>(),
            Mock.Of<IDeviceService>(),
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            TempBasalTestDeps.Unscheduled(),
            audit,
            NullLogger<TreatmentDecomposer>.Instance,
            reactor.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task DecomposeSensorChangeAsync() =>
        _decomposer.DecomposeAsync(
            new Treatment { Id = LegacyId, EventType = "Sensor Change", Mills = At, EnteredBy = "careportal" },
            WriteOrigin.Live);

    [Fact]
    public async Task DeletingTheTreatment_ReactsToItsDeviceEvent()
    {
        await DecomposeSensorChangeAsync();

        await _decomposer.DeleteByLegacyIdAsync(LegacyId, WriteOrigin.Live);

        _deleted.Should().ContainSingle().Which.Should().Match<V4Models.DeviceEvent>(
            e => e.EventType == DeviceEventType.SensorChange && e.LegacyId == LegacyId);
    }

    [Fact]
    public async Task ABackfillDelete_RunsNoReaction()
    {
        await DecomposeSensorChangeAsync();

        await _decomposer.DeleteByLegacyIdAsync(LegacyId, WriteOrigin.Backfill);

        _deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task ASourceReconciliationDelete_ReactsToItsDeviceEvent()
    {
        await DecomposeSensorChangeAsync();
        var source = _context.DeviceEvents.Single().DataSource!;

        await _decomposer.DeleteFromSourceAsync(source, new HashSet<string> { LegacyId });

        _deleted.Should().ContainSingle().Which.LegacyId.Should().Be(LegacyId);
    }
}
