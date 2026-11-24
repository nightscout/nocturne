using Nocturne.API.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A re-sent legacy record is written under the correlation id its stored rows already carry
/// (<see cref="DecomposerBase"/>'s stored-correlation lookup), so an unchanged re-send writes
/// nothing and a group whose rows disagree converges on one id.
/// </summary>
[Trait("Category", "Unit")]
public class ResendKeepsCorrelationIdTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const long At = 1_780_000_000_000;

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TestNocturneDbContextFactory _factory;
    private readonly IAuditContext _audit = Mock.Of<IAuditContext>();
    private readonly IDuplicateLinker _dedup = Mock.Of<IDuplicateLinker>();

    public ResendKeepsCorrelationIdTests()
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task Treatment_UnchangedResend_KeepsTheStoredIdAndWritesNothing(bool batch)
    {
        var decomposer = TreatmentDecomposerDoubles.Stored(_factory, _dedup, _audit);
        Treatment TempBasal() => new()
        {
            Id = "6500000000000000000000t1", EventType = "Temp Basal", Mills = At, Rate = 1.5, Absolute = 1.5, Duration = 30,
        };

        await decomposer.DecomposeOneAsync(TempBasal(), WriteOrigin.Live);
        var stored = await _context.TempBasals.AsNoTracking().SingleAsync();

        if (batch)
            await decomposer.DecomposeBatchAsync([TempBasal()], WriteOrigin.Live);
        else
            await decomposer.DecomposeOneAsync(TempBasal(), WriteOrigin.Live);

        var resent = await _context.TempBasals.AsNoTracking().SingleAsync();
        resent.CorrelationId.Should().Be(stored.CorrelationId);
        resent.SysUpdatedAt.Should().Be(stored.SysUpdatedAt, "an unchanged re-send is not an update");
    }

    [Fact]
    public async Task Treatment_ForkedMeal_ConvergesOnTheBolusId()
    {
        var decomposer = TreatmentDecomposerDoubles.Stored(_factory, _dedup, _audit);
        Treatment Meal() => new()
        {
            Id = "6500000000000000000000m1", EventType = "Meal Bolus", Mills = At, Insulin = 2, Carbs = 30,
        };

        await decomposer.DecomposeOneAsync(Meal(), WriteOrigin.Live);
        var bolusId = (await _context.Boluses.AsNoTracking().SingleAsync()).CorrelationId;
        await _context.CarbIntakes.ExecuteUpdateAsync(s => s.SetProperty(c => c.CorrelationId, Guid.CreateVersion7()));

        await decomposer.DecomposeBatchAsync([Meal()], WriteOrigin.Live);

        (await _context.Boluses.AsNoTracking().SingleAsync()).CorrelationId.Should().Be(bolusId);
        (await _context.CarbIntakes.AsNoTracking().SingleAsync()).CorrelationId.Should().Be(bolusId,
            "a sibling that drifted onto another id is stamped back onto the group's");
    }

    [Fact]
    public async Task Treatment_FirstSend_MintsAnId()
    {
        var result = await TreatmentDecomposerDoubles.Stored(_factory, _dedup, _audit).DecomposeBatchAsync(
            [new Treatment { Id = "6500000000000000000000n1", EventType = "Note", Mills = At, Notes = "hi" }],
            WriteOrigin.Live);

        var note = await _context.Notes.AsNoTracking().SingleAsync();
        note.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
        result.CorrelationId.Should().Be(note.CorrelationId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Entry_UnchangedResend_KeepsTheStoredIdAndWritesNothing(bool batch)
    {
        var decomposer = EntryDecomposerDoubles.Build(
            sensorGlucose: new SensorGlucoseRepository(_factory, _dedup, _audit, NullLogger<SensorGlucoseRepository>.Instance),
            meterGlucose: new MeterGlucoseRepository(_factory, _audit, NullLogger<MeterGlucoseRepository>.Instance),
            calibration: new CalibrationRepository(_factory, _audit, NullLogger<CalibrationRepository>.Instance),
            audit: _audit);
        Entry Sgv() => new() { Id = "sgv-1", Type = "sgv", Mills = At, Sgv = 120, Device = "cgm" };

        await decomposer.DecomposeAsync(Sgv(), WriteOrigin.Live);
        var stored = await _context.SensorGlucose.AsNoTracking().SingleAsync();

        if (batch)
            await decomposer.DecomposeBatchAsync([Sgv()], WriteOrigin.Live);
        else
            await decomposer.DecomposeAsync(Sgv(), WriteOrigin.Live);

        var resent = await _context.SensorGlucose.AsNoTracking().SingleAsync();
        resent.CorrelationId.Should().Be(stored.CorrelationId);
        resent.SysUpdatedAt.Should().Be(stored.SysUpdatedAt, "an unchanged re-send is not an update");
    }
}
