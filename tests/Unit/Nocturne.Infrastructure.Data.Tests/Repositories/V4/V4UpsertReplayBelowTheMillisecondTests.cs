using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// A record sent again with a time below the millisecond holds what its row stored, which the save cut
/// to the millisecond: every upsert path reads it as unchanged, leaves the row unwritten and reports no
/// update.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class V4UpsertReplayBelowTheMillisecondTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly DateTime At = new DateTime(2026, 6, 1, 8, 0, 0, 123, DateTimeKind.Utc).AddTicks(4560);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly List<RecordsChanged> _published = [];

    public V4UpsertReplayBelowTheMillisecondTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(Tenant, "tenant-c").WithClock(_clock);
        _context = _db.CreateContext();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The sync-keyed types refuse the one-by-one legacy-id upsert, so meter glucose, which has no sync key,
    /// takes that path.
    /// </summary>
    public static TheoryData<string, string> Cases => new()
    {
        { "sensor glucose", "bulk by legacy id" }, { "sensor glucose", "bulk by sync key" }, { "sensor glucose", "update" },
        { "bolus", "bulk by legacy id" }, { "bolus", "bulk by sync key" }, { "bolus", "update" },
        { "temp basal", "bulk by legacy id" }, { "temp basal", "bulk by sync key" }, { "temp basal", "update" },
        { "meter glucose", "by legacy id" }, { "meter glucose", "bulk by legacy id" }, { "meter glucose", "update" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ARecordSentAgainBelowTheMillisecond_IsUnchanged(string type, string path)
    {
        var linker = Mock.Of<IDuplicateLinker>();
        var audit = Mock.Of<IAuditContext>();
        var contexts = new TestNocturneDbContextFactory(_context);
        var changes = new Mock<IRecordChangePublisher>();
        changes.Setup(c => c.PublishAsync(It.IsAny<RecordsChanged>(), It.IsAny<CancellationToken>()))
            .Callback<RecordsChanged, CancellationToken>((batch, _) => _published.Add(batch))
            .Returns(Task.CompletedTask);
        switch (type)
        {
            case "sensor glucose":
                await ReplayAsync(
                    new SensorGlucoseRepository(contexts, linker, audit, NullLogger<SensorGlucoseRepository>.Instance, changes.Object),
                    path,
                    sync => new SensorGlucose { Timestamp = At, Mgdl = 120, DataSource = "aaps", SyncIdentifier = sync });
                break;
            case "bolus":
                await ReplayAsync(
                    new BolusRepository(contexts, linker, audit, NullLogger<BolusRepository>.Instance, changes.Object),
                    path,
                    sync => new Bolus { Timestamp = At, Insulin = 1.5, DataSource = "aaps", SyncIdentifier = sync });
                break;
            case "meter glucose":
                await ReplayAsync(
                    new MeterGlucoseRepository(contexts, audit, NullLogger<MeterGlucoseRepository>.Instance, changes.Object),
                    path,
                    _ => new MeterGlucose { Timestamp = At, Mgdl = 140, DataSource = "aaps" });
                break;
            default:
                await ReplayAsync(
                    new TempBasalRepository(contexts, linker, audit, NullLogger<TempBasalRepository>.Instance, changes.Object),
                    path,
                    sync => new TempBasal
                    {
                        StartTimestamp = At, EndTimestamp = At.AddMinutes(30), Rate = 0.8,
                        Origin = TempBasalOrigin.Algorithm, OriginEvidence = TempBasalOriginEvidence.Reported,
                        DataSource = "aaps", SyncIdentifier = sync,
                    });
                break;
        }
    }

    /// <summary>Writes the record, then sends it again by <paramref name="path"/> five minutes later.</summary>
    private async Task ReplayAsync<TModel, TEntity>(
        V4RepositoryBase<TModel, TEntity> repository, string path, Func<string?, TModel> record)
        where TModel : class, IV4Record
        where TEntity : class, IV4TimeSeriesEntity, IAuditable, ISystemTimestamped
    {
        var bySyncKey = path == "bulk by sync key";
        TModel Sent()
        {
            var sent = record(bySyncKey ? "sync-1" : null);
            sent.LegacyId = bySyncKey ? null : "legacy-1";
            return sent;
        }

        var first = (await repository.BulkUpsertAsync([Sent()], WriteOrigin.Live)).Single();
        var stamped = await SysUpdatedAtAsync<TEntity>(first.Id);
        _clock.Advance(TimeSpan.FromMinutes(5));

        switch (path)
        {
            case "by legacy id":
                (await repository.BulkUpsertByLegacyIdAsync([Sent()], WriteOrigin.Live)).Outcomes["legacy-1"].Outcome
                    .Should().Be(UpsertOutcome.Unchanged);
                break;
            case "update":
                await repository.UpdateAsync(first.Id, Sent(), WriteOrigin.Live);
                break;
            default:
                (await repository.BulkUpsertAsync([Sent()], WriteOrigin.Live)).Unchanged.Should().ContainSingle();
                break;
        }

        (await SysUpdatedAtAsync<TEntity>(first.Id)).Should().Be(stamped);
        _published.SelectMany(b => b.Changes).SelectMany(c => c.Updated).Should().BeEmpty();
    }

    private async Task<DateTime> SysUpdatedAtAsync<TEntity>(Guid id)
        where TEntity : class, IV4TimeSeriesEntity, ISystemTimestamped
    {
        await using var db = _db.CreateContext();
        return await db.Set<TEntity>().AsNoTracking().Where(e => e.Id == id).Select(e => e.SysUpdatedAt).SingleAsync();
    }
}
