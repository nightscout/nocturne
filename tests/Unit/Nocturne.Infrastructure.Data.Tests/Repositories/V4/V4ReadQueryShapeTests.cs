using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// The SQL the V4 repositories' reads send, on the Npgsql provider with no database: the WHERE and
/// ORDER BY of a read is what a keyset cursor and the partial read indexes depend on.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class V4ReadQueryShapeTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(1);
    private static readonly Guid CursorId = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid PatientDeviceId = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private const string Tenant = "t.tenant_id = @ef_filter__TenantId AND t.deleted_at IS NULL";
    private const string Window = "t.timestamp >= @from_Value AND t.timestamp <= @to_Value AND t.device = @device AND t.data_source = @source";
    private const string DescendingCursor = "(t.timestamp < @afterTimestamp_Value OR (t.timestamp = @afterTimestamp_Value AND t.id < @afterId_Value))";

    private readonly CapturedSql _sql = new();
    private readonly NocturneDbContext _context;
    private readonly INocturneDbContextFactory _factory;
    private readonly IDuplicateLinker _linker = new Mock<IDuplicateLinker>().Object;
    private readonly IAuditContext _audit = new Mock<IAuditContext>().Object;

    public V4ReadQueryShapeTests()
    {
        _context = _sql.CreateContext();
        _factory = new TestNocturneDbContextFactory(_context);
    }

    private string Where(char alias) =>
        _sql.Commands[^1].Split('\n').Single(line => line.StartsWith("WHERE ")).TrimEnd().Replace($"{alias}.", "t.");

    private string OrderBy() =>
        _sql.Commands[^1].Split('\n').Single(line => line.StartsWith("ORDER BY ")).TrimEnd();

    [Fact]
    public async Task DefaultRead_OfEveryDuplicateFlaggedType_HidesNonPrimaryCopies()
    {
        await Default(new BGCheckRepository(_factory, _linker, _audit, NullLogger<BGCheckRepository>.Instance), 'b');
        await Default(new BolusCalculationRepository(_factory, _linker, _audit, NullLogger<BolusCalculationRepository>.Instance), 'b');
        await Default(new BolusRepository(_factory, _linker, _audit, NullLogger<BolusRepository>.Instance), 'b');
        await Default(new CarbIntakeRepository(_factory, _linker, _audit, NullLogger<CarbIntakeRepository>.Instance), 'c');
        await Default(new DeviceEventRepository(_factory, _linker, _audit, NullLogger<DeviceEventRepository>.Instance), 'd');
        await Default(new NoteRepository(_factory, _linker, _audit, NullLogger<NoteRepository>.Instance), 'n');
        await Default(new SensorGlucoseRepository(_factory, _linker, _audit, NullLogger<SensorGlucoseRepository>.Instance), 's');
        await Default(new TempBasalRepository(_factory, _linker, _audit, NullLogger<TempBasalRepository>.Instance), 't');
    }

    [Fact]
    public async Task DefaultRead_OfATypeThatIsNotDuplicateFlagged_IsNotFiltered()
    {
        var repo = new BasalInjectionRepository(_factory, _audit, NullLogger<BasalInjectionRepository>.Instance);

        await Default(repo, 'b', hidesDuplicates: false);
    }

    [Fact]
    public async Task BolusRead_WithKindAndCursor_FiltersKindBeforeDuplicatesThenSeeksFromTheCursor()
    {
        var repo = new BolusRepository(_factory, _linker, _audit, NullLogger<BolusRepository>.Instance);

        await ((IBolusRepository)repo).GetAsync(From, To, "dev", "src", 50, 0, true, BolusKind.Manual, From, CursorId);

        Where('b').Should().Be(
            $"WHERE {Tenant} AND {Window} AND t.bolus_kind = @ToString AND NOT (t.is_duplicate) AND {DescendingCursor}");
        OrderBy().Should().Be("ORDER BY b.timestamp DESC, b.id DESC");
    }

    [Fact]
    public async Task CarbIntakeRead_AscendingWithCursor_SeeksPastTheCursor()
    {
        var repo = new CarbIntakeRepository(_factory, _linker, _audit, NullLogger<CarbIntakeRepository>.Instance);

        await ((ICarbIntakeRepository)repo).GetAsync(From, To, "dev", "src", 50, 0, false, From, CursorId);

        Where('c').Should().Be(
            $"WHERE {Tenant} AND {Window} AND NOT (t.is_duplicate)"
            + " AND (t.timestamp > @afterTimestamp_Value OR (t.timestamp = @afterTimestamp_Value AND t.id > @afterId_Value))");
        OrderBy().Should().Be("ORDER BY c.timestamp, c.id");
    }

    [Fact]
    public async Task CursorRead_ReplacesOffset()
    {
        var repo = new CarbIntakeRepository(_factory, _linker, _audit, NullLogger<CarbIntakeRepository>.Instance);

        await ((ICarbIntakeRepository)repo).GetAsync(From, To, null, null, 50, 7, true, From, CursorId);
        _sql.Commands[^1].Should().NotContain("OFFSET");

        await ((ICarbIntakeRepository)repo).GetAsync(From, To, null, null, 50, 7, true, From, null);
        _sql.Commands[^1].Should().Contain("OFFSET");
    }

    [Fact]
    public async Task SensorGlucoseRead_WithPatientDeviceStreamsAndCursor_AppliesEachOnce()
    {
        var repo = new SensorGlucoseRepository(_factory, _linker, _audit, NullLogger<SensorGlucoseRepository>.Instance);

        await ((ISensorGlucoseRepository)repo).GetAsync(
            From, To, "dev", "src", 50, 0, true, From, CursorId, default, PatientDeviceId,
            GlucoseStreams.CanonicalExcludingEphemeral);

        Where('s').Should().Be(
            $"WHERE {Tenant} AND {Window} AND t.patient_device_id = @patientDeviceId_Value"
            + " AND NOT (t.is_secondary_stream) AND (t.data_source IS NULL OR t.data_source NOT IN ('demo-service', 'testing'))"
            + $" AND NOT (t.is_duplicate) AND {DescendingCursor}");
    }

    [Fact]
    public async Task SensorGlucoseByValueRead_OrdersByValueThenObservationOrder()
    {
        var repo = new SensorGlucoseRepository(_factory, _linker, _audit, NullLogger<SensorGlucoseRepository>.Instance);

        await ((ISensorGlucoseRepository)repo).GetForEntriesByValueAsync(From, To, "src", 20, 0, true);

        OrderBy().Should().Be("ORDER BY s.mgdl DESC, s.timestamp DESC, s.id DESC");
        Where('s').Should().Be(
            $"WHERE {Tenant} AND t.timestamp >= @from_Value AND t.timestamp <= @to_Value"
            + " AND t.data_source = @source AND NOT (t.is_duplicate)");
    }

    [Fact]
    public async Task DeviceEventRead_WithPatientDevice_FiltersItBeforeDuplicates()
    {
        var repo = new DeviceEventRepository(_factory, _linker, _audit, NullLogger<DeviceEventRepository>.Instance);

        await ((IDeviceEventRepository)repo).GetAsync(From, To, "dev", "src", 50, 0, true, PatientDeviceId);

        Where('d').Should().Be(
            $"WHERE {Tenant} AND {Window} AND t.patient_device_id = @patientDeviceId_Value AND NOT (t.is_duplicate)");
    }

    private async Task Default<TModel>(IV4Repository<TModel> repo, char alias, bool hidesDuplicates = true)
        where TModel : class, IV4Record
    {
        await repo.GetAsync(null, null, null, null, 100, 0, true);

        Where(alias).Should().Be(hidesDuplicates ? $"WHERE {Tenant} AND NOT (t.is_duplicate)" : $"WHERE {Tenant}");
    }
}
