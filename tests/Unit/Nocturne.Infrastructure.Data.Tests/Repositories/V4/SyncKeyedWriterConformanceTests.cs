using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// The writers that used to sit beside the shared sync-key upsert — the temp basal repository's
/// own copy, and the note, device event and BG check repositories that took keyed deletes (or
/// carried the column) without upserting on the key — now go through it.
/// </summary>
/// <remarks>
/// SQLite, so the partial unique index <c>EnsureCreated</c> builds is enforced: a second live row
/// under one key would fail the insert rather than pass unnoticed.
/// </remarks>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class SyncKeyedWriterConformanceTests : IDisposable
{
    private const string DataSource = "aaps";
    private const string SyncIdentifier = "sync-1";

    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly DateTime T0 = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;

    public SyncKeyedWriterConformanceTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(Tenant, "tenant-b");
        _context = _db.CreateContext();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private TestTenantDbContextFactory Factory => new(_context);

    private TempBasalRepository TempBasals() =>
        new(Factory, new Mock<IDeduplicationService>().Object, new Mock<IAuditContext>().Object,
            NullLogger<TempBasalRepository>.Instance);

    private NoteRepository Notes() =>
        new(Factory, new Mock<IDeduplicationService>().Object, new Mock<IAuditContext>().Object,
            NullLogger<NoteRepository>.Instance);

    private DeviceEventRepository DeviceEvents() =>
        new(Factory, new Mock<IDeduplicationService>().Object, new Mock<IAuditContext>().Object,
            NullLogger<DeviceEventRepository>.Instance);

    private BGCheckRepository BGChecks() =>
        new(Factory, new Mock<IDeduplicationService>().Object, new Mock<IAuditContext>().Object,
            NullLogger<BGCheckRepository>.Instance);

    private static TempBasal TempBasal(double rate) => new()
    {
        StartTimestamp = T0,
        Rate = rate,
        Origin = TempBasalOrigin.Algorithm,
        DataSource = DataSource,
        SyncIdentifier = SyncIdentifier,
    };

    private static Note Note(string text) => new()
    {
        Timestamp = T0, Text = text, DataSource = DataSource, SyncIdentifier = SyncIdentifier,
    };

    private static DeviceEvent DeviceEvent(string notes) => new()
    {
        Timestamp = T0, EventType = DeviceEventType.SiteChange, Notes = notes,
        DataSource = DataSource, SyncIdentifier = SyncIdentifier,
    };

    private static BGCheck BGCheck(double glucose) => new()
    {
        Timestamp = T0, Glucose = glucose, DataSource = DataSource, SyncIdentifier = SyncIdentifier,
    };

    private void MarkDeletedByUser<TEntity>(Guid id) where TEntity : class, IV4TimeSeriesEntity
    {
        using var ctx = _db.CreateContext();
        var entity = ctx.Set<TEntity>().IgnoreQueryFilters().Single(e => e.Id == id);
        entity.DeletedAt = T0.AddHours(1);
        ctx.Entry(entity).Property("DeletedByUser").CurrentValue = true;
        ctx.SaveChanges();
    }

    private List<TEntity> AllRows<TEntity>() where TEntity : class, IV4TimeSeriesEntity
    {
        using var ctx = _db.CreateContext();
        return ctx.Set<TEntity>().IgnoreQueryFilters().AsNoTracking().ToList();
    }

    [Fact]
    public async Task TempBasal_Create_WhenAUserDeletedTombstoneHoldsTheKey_IsRefused()
    {
        var deleted = await TempBasals().CreateAsync(TempBasal(1.0), WriteOrigin.Live);
        MarkDeletedByUser<TempBasalEntity>(deleted.Id);

        var act = () => TempBasals().CreateAsync(TempBasal(2.0), WriteOrigin.Live);

        await act.Should().ThrowAsync<RecreationBlockedException>().WithMessage($"*{SyncIdentifier}*");
        AllRows<TempBasalEntity>().Should().ContainSingle().Which.Id.Should().Be(deleted.Id);
    }

    [Fact]
    public async Task TempBasal_BulkCreate_UpdatesTheStoredKeyInPlace_KeepingWhatTheWriteCannotExpress()
    {
        var seed = TempBasal(1.0);
        seed.LegacyId = "legacy-1";
        seed.InsulinContext = new TreatmentInsulinContext { InsulinName = "Fiasp", Dia = 6, Peak = 55 };
        var stored = await TempBasals().CreateAsync(seed, WriteOrigin.Live);

        var written = await TempBasals().BulkCreateAsync([TempBasal(2.5)], WriteOrigin.Live);

        written.Should().ContainSingle().Which.Id.Should().Be(stored.Id);
        var row = AllRows<TempBasalEntity>().Should().ContainSingle().Subject;
        row.Rate.Should().Be(2.5);
        row.LegacyId.Should().Be("legacy-1");
        row.InsulinContextJson.Should().Contain("Fiasp");
    }

    [Fact]
    public Task Note_CreateUpsertsOnTheKey() =>
        AssertCreateUpsertsAsync<Note, NoteEntity>(Notes(), Note("first"), Note("second"), e => e.Text, "second");

    [Fact]
    public Task DeviceEvent_CreateUpsertsOnTheKey() =>
        AssertCreateUpsertsAsync<DeviceEvent, DeviceEventEntity>(
            DeviceEvents(), DeviceEvent("first"), DeviceEvent("second"), e => e.Notes, "second");

    [Fact]
    public Task BGCheck_CreateUpsertsOnTheKey() =>
        AssertCreateUpsertsAsync<BGCheck, BGCheckEntity>(BGChecks(), BGCheck(100), BGCheck(140), e => e.Glucose, 140.0);

    [Fact]
    public Task Note_BulkCreateUpsertsOnTheKey() =>
        AssertBulkUpsertsAsync<Note, NoteEntity>(Notes(), Note("first"), Note("second"), e => e.Text, "second");

    [Fact]
    public Task DeviceEvent_BulkCreateUpsertsOnTheKey() =>
        AssertBulkUpsertsAsync<DeviceEvent, DeviceEventEntity>(
            DeviceEvents(), DeviceEvent("first"), DeviceEvent("second"), e => e.Notes, "second");

    [Fact]
    public Task BGCheck_BulkCreateUpsertsOnTheKey() =>
        AssertBulkUpsertsAsync<BGCheck, BGCheckEntity>(BGChecks(), BGCheck(100), BGCheck(140), e => e.Glucose, 140.0);

    [Fact]
    public async Task BGCheck_Create_WhenAUserDeletedTombstoneHoldsTheKey_IsRefused()
    {
        var deleted = await BGChecks().CreateAsync(BGCheck(100), WriteOrigin.Live);
        MarkDeletedByUser<BGCheckEntity>(deleted.Id);

        var act = () => BGChecks().CreateAsync(BGCheck(140), WriteOrigin.Live);

        await act.Should().ThrowAsync<RecreationBlockedException>();
        (await BGChecks().IsRecreationBlockedAsync(DataSource, SyncIdentifier)).Should().BeTrue();
    }

    [Fact]
    public async Task Note_KeyedDelete_RemovesTheOneRowTheKeyNames()
    {
        await Notes().CreateAsync(Note("first"), WriteOrigin.Live);
        await Notes().CreateAsync(Note("second"), WriteOrigin.Live);

        (await Notes().DeleteBySyncIdentifierAsync(DataSource, SyncIdentifier, WriteOrigin.Live)).Should().Be(1);
    }

    [Fact]
    public async Task DeviceEvent_KeyedDelete_RemovesTheOneRowTheKeyNames()
    {
        await DeviceEvents().CreateAsync(DeviceEvent("first"), WriteOrigin.Live);
        await DeviceEvents().CreateAsync(DeviceEvent("second"), WriteOrigin.Live);

        (await DeviceEvents().DeleteBySyncIdentifierAsync(DataSource, SyncIdentifier, WriteOrigin.Live)).Should().Be(1);
    }

    /// <summary>
    /// A keyed delete is only "the record this key names" when the key is unique, so every entity a
    /// keyed repository serves has to carry the index.
    /// </summary>
    [Fact]
    public void EveryKeyedRepositoryEntityCarriesTheSyncKeyIndex()
    {
        var keyedEntities = typeof(SyncKeyedRepositoryBase<,>).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Select(KeyedEntityOf)
            .OfType<Type>()
            .ToList();

        keyedEntities.Should().Contain([typeof(NoteEntity), typeof(DeviceEventEntity), typeof(BGCheckEntity), typeof(TempBasalEntity)]);
        keyedEntities.Should().BeSubsetOf(NocturneDbContext.SyncDedupedEntities);
    }

    private static Type? KeyedEntityOf(Type type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(SyncKeyedRepositoryBase<,>))
                return t.GetGenericArguments()[1];
        }

        return null;
    }

    private async Task AssertCreateUpsertsAsync<TModel, TEntity>(
        V4RepositoryBase<TModel, TEntity> repository, TModel first, TModel resent,
        Func<TEntity, object?> value, object expected)
        where TModel : class, IV4Record
        where TEntity : class, IV4TimeSeriesEntity, IAuditable, ISystemTimestamped
    {
        var stored = await repository.CreateAsync(first, WriteOrigin.Live);

        var upserted = await repository.CreateAsync(resent, WriteOrigin.Live);

        upserted.Id.Should().Be(stored.Id);
        value(AllRows<TEntity>().Should().ContainSingle().Subject).Should().Be(expected);
    }

    private async Task AssertBulkUpsertsAsync<TModel, TEntity>(
        V4RepositoryBase<TModel, TEntity> repository, TModel first, TModel resent,
        Func<TEntity, object?> value, object expected)
        where TModel : class, IV4Record
        where TEntity : class, IV4TimeSeriesEntity, IAuditable, ISystemTimestamped
    {
        var stored = await repository.CreateAsync(first, WriteOrigin.Live);

        var written = await repository.BulkCreateAsync([resent], WriteOrigin.Live);

        written.Should().ContainSingle().Which.Id.Should().Be(stored.Id);
        value(AllRows<TEntity>().Should().ContainSingle().Subject).Should().Be(expected);
    }
}
