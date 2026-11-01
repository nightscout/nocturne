using Nocturne.API.Tests.TestDoubles;
using Nocturne.API.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A span the user deleted holds its treatment id against a re-upload only while its category is a
/// current <see cref="StateSpanCategory"/>; a retired one holds nothing.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentDecomposerRetiredSpanTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const long At = 1_780_000_000_000;
    private const string Id = "tt-1";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TestNocturneDbContextFactory _factory;
    private readonly AuditContext _audit = new();
    private readonly IDeduplicationService _dedup = Mock.Of<IDeduplicationService>();

    public TreatmentDecomposerRetiredSpanTests()
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

    public static TheoryData<string, bool> Tombstones() => new()
    {
        { "TempBasal", false },
        { "BasalDelivery", false },
        { nameof(StateSpanCategory.TemporaryTarget), true },
    };

    [Theory]
    [MemberData(nameof(Tombstones))]
    public async Task A_user_deleted_span_blocks_the_reupload_only_while_its_category_is_current(string category, bool blocks)
    {
        await SeedUserDeletedSpanAsync(category);

        var result = await Decomposer().DecomposeOneAsync(new Treatment
        {
            Id = Id, EventType = "Temporary Target", Mills = At, Duration = 60,
            TargetTop = 80, TargetBottom = 80, Units = "mg/dl", EnteredBy = "AAPS",
        }, WriteOrigin.Live);

        result.SkippedDeleted.Should().Be(blocks ? 1 : 0);
        (await _context.StateSpans.AsNoTracking().CountAsync(s => s.OriginalId == Id)).Should().Be(blocks ? 0 : 1);
    }

    [Theory]
    [MemberData(nameof(Tombstones))]
    public async Task A_user_deleted_span_reads_as_deleted_by_the_user_only_while_its_category_is_current(string category, bool blocks)
    {
        await SeedUserDeletedSpanAsync(category);

        var deleted = await Decomposer().GetDeletedByUserAsync([Id]);

        deleted.Contains(Id).Should().Be(blocks);
    }

    private async Task SeedUserDeletedSpanAsync(string category)
    {
        var span = new StateSpanEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Category = category,
            State = "Active",
            Source = "AAPS",
            OriginalId = Id,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(At).UtcDateTime,
        };
        _context.StateSpans.Add(span);
        await _context.SaveChangesAsync();
        _context.Entry(span).State = EntityState.Detached;
        await _context.StateSpans.IgnoreQueryFilters().Where(s => s.Id == span.Id).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.DeletedAt, DateTime.UtcNow)
            .SetProperty(s => EF.Property<bool>(s, "DeletedByUser"), true));
    }

    private TreatmentDecomposer Decomposer()
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
            new StateSpanService(
                new StateSpanRepository(_factory, _dedup, _audit, NullLogger<StateSpanRepository>.Instance),
                NullLogger<StateSpanService>.Instance),
            ProfileActivationTestDeps.Repository(_context),
            ProfileActivationTestDeps.Writer(_context),
            foods.Object,
            new UnresolvedDevices(),
            Mock.Of<IPatientDeviceStamper>(),
            new Mock<IActiveProfileResolver>().AnswersInsulinContexts(_ => null).Object,
            Mock.Of<IPatientInsulinRepository>(),
            TempBasalTestDeps.Unscheduled(),
            _audit,
            NullLogger<TreatmentDecomposer>.Instance);
    }
}
