using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.Connectors.CareLink.Mappers;
using Nocturne.Connectors.CareLink.Models;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

/// <summary>
/// CareLink payloads published through <see cref="TreatmentPublisher"/> into a real temp basal
/// repository on SQLite, as consecutive syncs reporting overlapping windows.
/// </summary>
[Trait("Category", "Unit")]
public class CareLinkTempBasalResyncTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Now = new(2026, 6, 10, 12, 30, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TreatmentPublisher _publisher;

    public CareLinkTempBasalResyncTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(Tenant);
        _context = _db.CreateContext();
        var audit = new SystemAuditContext();
        var tempBasals = new TempBasalRepository(
            new TestNocturneDbContextFactory(_context),
            Mock.Of<IDuplicateLinker>(),
            audit,
            NullLogger<TempBasalRepository>.Instance);

        _publisher = new TreatmentPublisher(
            Mock.Of<ITreatmentService>(),
            Mock.Of<IBolusRepository>(),
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBGCheckRepository>(),
            Mock.Of<IBolusCalculationRepository>(),
            tempBasals,
            Mock.Of<IBasalInjectionRepository>(),
            Mock.Of<INoteRepository>(),
            Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IPatientInsulinRepository>(),
            Mock.Of<IBasalRateResolver>(),
            Mock.Of<ITherapySettingsResolver>(),
            Mock.Of<IPatientDeviceStamper>(),
            audit,
            new PublishSkipTally(),
            Mock.Of<ITenantAccessor>(),
            NullLogger<TreatmentPublisher>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CareLinkMarker AutoBasal(string dateTime, double amount) =>
        new() { Type = "AUTO_BASAL_DELIVERY", DateTime = dateTime, BolusAmount = amount };

    private static readonly CareLinkMarker[] Window =
    [
        AutoBasal("2026-06-10T11:54:39", 0.275),
        AutoBasal("2026-06-10T11:59:39", 0.1),
        AutoBasal("2026-06-10T12:04:39", 0.15),
    ];

    private Task SyncAsync(params CareLinkMarker[] markers) => SyncAtAsync(Now, markers);

    private async Task SyncAtAsync(DateTime now, params CareLinkMarker[] markers)
    {
        var data = new CareLinkData { MedicalDeviceFamily = "780G", Markers = [.. markers] };
        var published = await _publisher.PublishTempBasalsAsync(
            CareLinkTreatmentMapper.MapTempBasals(data, 0, now), DataSources.CareLinkConnector, WriteOrigin.Live);
        published.Should().BeTrue();
    }

    [Fact]
    public async Task Two_syncs_of_one_payload_keep_one_live_row_per_marker_and_delete_nothing()
    {
        await SyncAsync(Window);
        var first = await _context.TempBasals.AsNoTracking().Select(t => t.Id).ToListAsync();

        await SyncAsync(Window);

        var rows = await _context.TempBasals.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().HaveCount(Window.Length);
        rows.Should().OnlyContain(t => t.DeletedAt == null);
        rows.Select(t => t.Id).Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task A_later_sync_of_an_unchanged_payload_writes_nothing()
    {
        await SyncAsync(Window);
        var first = await _context.TempBasals.AsNoTracking()
            .ToDictionaryAsync(t => t.Id, t => t.SysUpdatedAt);

        await SyncAtAsync(Now.AddMinutes(5), Window);

        var rows = await _context.TempBasals.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().HaveCount(Window.Length);
        rows.Should().OnlyContain(t => t.DeletedAt == null);
        rows.ToDictionary(t => t.Id, t => t.SysUpdatedAt).Should().Equal(first);
    }

    [Fact]
    public async Task A_marker_whose_span_changes_updates_its_row_in_place()
    {
        await SyncAsync(Window);
        var last = await _context.TempBasals.AsNoTracking().OrderBy(t => t.Timestamp).LastAsync();

        await SyncAsync([.. Window, AutoBasal("2026-06-10T12:07:39", 0.05)]);

        var rows = await _context.TempBasals.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().HaveCount(Window.Length + 1);
        rows.Should().OnlyContain(t => t.DeletedAt == null);
        var updated = rows.Single(t => t.Id == last.Id);
        updated.EndTimestamp.Should().Be(new DateTime(2026, 6, 10, 12, 7, 39, DateTimeKind.Utc));
        updated.Rate.Should().BeApproximately(0.15 * 60 / 3, 1e-9);
    }
}
