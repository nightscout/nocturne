using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services.Deduplication;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// The tables deduplication covers, the order a full run passes over them, and that every per-type
/// read and write of <see cref="LinkedRecordTypes"/> and <see cref="DuplicateGroupPrimaries"/> reaches
/// each of them.
/// </summary>
[Trait("Category", "Unit")]
public class DeduplicatedTableListTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime At = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TheoryData<RecordType> DeduplicatedRecordTypes => [.. LinkedRecordTypes.DeduplicatedRecordTypes];

    [Fact]
    public void AFullRunPassesOverTheTypesInTheirOrder()
    {
        LinkedRecordTypes.TypePhases.Select(p => (p.RecordType, p.Name)).Should().Equal(
            (RecordType.SensorGlucose, "SensorGlucose"),
            (RecordType.Bolus, "Boluses"),
            (RecordType.CarbIntake, "CarbIntakes"),
            (RecordType.BGCheck, "BGChecks"),
            (RecordType.DeviceEvent, "DeviceEvents"),
            (RecordType.Note, "Notes"),
            (RecordType.BolusCalculation, "BolusCalculations"),
            (RecordType.TempBasal, "TempBasals"),
            (RecordType.StateSpan, "StateSpans"));
    }

    [Fact]
    public void ARegistryEntryBuildsItsPhaseWithoutTheRunsPhasesHavingBeenRead()
    {
        V4RecordTables.Deduplicated.Select(t => t.CreatePhase())
            .Select(p => (p.RecordType, p.Name))
            .Should().Equal(LinkedRecordTypes.TypePhases.Select(p => (p.RecordType, p.Name)));
    }

    [Fact]
    public void EachDeduplicatedEntityIsLinkedUnderItsRecordTypeAndNoOtherEntityIs()
    {
        var linked = typeof(NocturneDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .Distinct()
            .Select(entity => (entity, recordType: DuplicateGroupPrimaries.RecordTypeOf(entity)))
            .Where(pair => pair.recordType is not null)
            .ToDictionary(pair => pair.entity, pair => pair.recordType!.Value);

        linked.Should().BeEquivalentTo(new Dictionary<Type, RecordType>
        {
            [typeof(SensorGlucoseEntity)] = RecordType.SensorGlucose,
            [typeof(BolusEntity)] = RecordType.Bolus,
            [typeof(CarbIntakeEntity)] = RecordType.CarbIntake,
            [typeof(BGCheckEntity)] = RecordType.BGCheck,
            [typeof(DeviceEventEntity)] = RecordType.DeviceEvent,
            [typeof(NoteEntity)] = RecordType.Note,
            [typeof(BolusCalculationEntity)] = RecordType.BolusCalculation,
            [typeof(TempBasalEntity)] = RecordType.TempBasal,
            [typeof(StateSpanEntity)] = RecordType.StateSpan,
        });
    }

    [Theory]
    [MemberData(nameof(DeduplicatedRecordTypes))]
    public async Task TheRecordInfoOfEveryTypeReadsSoftDeletedRows(RecordType recordType)
    {
        var id = await AddAsync(recordType, deleted: true);

        await using var context = _db.CreateContext();
        var info = await LinkedRecordTypes.LoadRecordInfoAsync(context, recordType, [id], default);

        info.Should().ContainKey(id).WhoseValue.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task AStateSpansRecordInfoCarriesHowItEnded()
    {
        var id = await AddAsync(RecordType.StateSpan, deleted: false);

        await using var context = _db.CreateContext();
        var info = await LinkedRecordTypes.LoadRecordInfoAsync(context, RecordType.StateSpan, [id], default);

        info[id].Span.Should().Be(new DuplicateMatching.SpanEnd(null, At.AddHours(1)));
    }

    [Theory]
    [MemberData(nameof(DeduplicatedRecordTypes))]
    public async Task TheAuditFindsAndRepairsAWrongFlagOfEveryType(RecordType recordType)
    {
        var id = await AddAsync(recordType, deleted: true, duplicate: true);

        await using var context = _db.CreateContext();
        var pages = new List<DisagreeingRow[]>();
        await foreach (var page in DuplicateGroupPrimaries.DisagreeingPagesAsync(context, recordType, 10, default))
            pages.Add(page);
        pages.SelectMany(p => p).Should().Equal(new DisagreeingRow(id, true));

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            (await DuplicateGroupPrimaries.RepairFlagsAsync(context, recordType, [id], default)).Should().Be(1);
            await transaction.CommitAsync();
        }
    }

    [Theory]
    [MemberData(nameof(DeduplicatedRecordTypes))]
    public async Task ARepointPromotesALiveCopyOfEveryTypeAndStampsItWhereTheTypeIsStamped(RecordType recordType)
    {
        var deleted = await AddAsync(recordType, deleted: true);
        var live = await AddAsync(recordType, deleted: false);
        await using (var linking = _db.CreateContext())
            await DuplicateGroups.LinkAsync(linking, recordType, deleted, live);
        var touchAt = At.AddDays(30);

        await using var context = _db.CreateContext();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            (await DuplicateGroupPrimaries.RepointAwayFromAsync(context, recordType, [deleted], touchAt, default))
                .Should().Equal(live);
            await transaction.CommitAsync();
        }

        if (recordType == RecordType.StateSpan)
            return;

        await using var verify = _db.CreateContext();
        var promoted = (ISystemTimestamped)(await verify.FindAsync(EntityOf(recordType), live))!;
        promoted.SysUpdatedAt.Should().Be(touchAt);
    }

    private static Type EntityOf(RecordType recordType) => recordType switch
    {
        RecordType.SensorGlucose => typeof(SensorGlucoseEntity),
        RecordType.Bolus => typeof(BolusEntity),
        RecordType.CarbIntake => typeof(CarbIntakeEntity),
        RecordType.BGCheck => typeof(BGCheckEntity),
        RecordType.DeviceEvent => typeof(DeviceEventEntity),
        RecordType.Note => typeof(NoteEntity),
        RecordType.BolusCalculation => typeof(BolusCalculationEntity),
        RecordType.TempBasal => typeof(TempBasalEntity),
        RecordType.StateSpan => typeof(StateSpanEntity),
        _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, null),
    };

    private async Task<Guid> AddAsync(RecordType recordType, bool deleted, bool duplicate = false)
    {
        var id = Guid.CreateVersion7();
        DateTime? deletedAt = deleted ? At : null;
        object entity = recordType switch
        {
            RecordType.SensorGlucose => new SensorGlucoseEntity { Id = id, Mgdl = 120, Timestamp = At, DataSource = "c", DeletedAt = deletedAt },
            RecordType.Bolus => new BolusEntity { Id = id, Insulin = 2, Timestamp = At, DataSource = "c", DeletedAt = deletedAt },
            RecordType.CarbIntake => new CarbIntakeEntity { Id = id, Carbs = 30, Timestamp = At, DataSource = "c", DeletedAt = deletedAt },
            RecordType.BGCheck => new BGCheckEntity { Id = id, Glucose = 120, Timestamp = At, DataSource = "c", DeletedAt = deletedAt },
            RecordType.DeviceEvent => new DeviceEventEntity { Id = id, EventType = "SiteChange", Timestamp = At, DataSource = "c", DeletedAt = deletedAt },
            RecordType.Note => new NoteEntity { Id = id, Text = "note", Timestamp = At, DataSource = "c", DeletedAt = deletedAt },
            RecordType.BolusCalculation => new BolusCalculationEntity { Id = id, CarbInput = 30, Timestamp = At, DataSource = "c", DeletedAt = deletedAt },
            RecordType.TempBasal => new TempBasalEntity { Id = id, Rate = 0.5, Timestamp = At, Origin = "Manual", DataSource = "c", DeletedAt = deletedAt },
            RecordType.StateSpan => new StateSpanEntity
            {
                Id = id, Category = nameof(StateSpanCategory.Exercise), State = "Running", StartTimestamp = At,
                EndTimestamp = At.AddHours(1), Source = "c", DeletedAt = deletedAt,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, null),
        };
        ((ITenantScoped)entity).TenantId = TenantId;

        await using var context = _db.CreateContext();
        context.Add(entity);
        await context.SaveChangesAsync();
        if (duplicate)
            await (Task<int>)typeof(DeduplicatedTableListTests)
                .GetMethod(nameof(FlagAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(entity.GetType())
                .Invoke(null, [context, id])!;
        return id;
    }

    private static Task<int> FlagAsync<TEntity>(NocturneDbContext context, Guid id)
        where TEntity : class, IDuplicateFlagged =>
        context.Set<TEntity>().IgnoreQueryFilters().Where(e => e.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDuplicate, true));
}
