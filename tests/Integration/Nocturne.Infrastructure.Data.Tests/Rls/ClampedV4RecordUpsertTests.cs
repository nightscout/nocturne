using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A history-clamped uploader re-sending a V4 record whose sync key or legacy id a row before its window
/// holds gets the answer an unclamped uploader gets, instead of 23505 from the tenant-wide unique index.
/// Neither the answer nor any broadcast carries a field only the stored row holds, and the row stays in
/// its correlation group.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ClampedV4RecordUpsertTests(RlsCompletenessFixture fx) : IAsyncLifetime
{
    private static readonly DateTime StoredCreatedAt = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private ServiceProvider _provider = null!;
    private Guid _tenant;
    private readonly CapturingPublisher _published = new();
    private readonly CapturingEntrySink _projected = new();

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task SyncKey_StoredBeforeTheWindow_IsUpdatedAndAnsweredAndBroadcastAsSent()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        var storedId = await SeedSensorGlucoseAsync(timestamp, "stored-legacy-id", "xdrip", "reading-1");
        await using var scope = ClampedScope();

        var written = await SensorGlucose(scope).UpsertAsync(
            Reading(timestamp, 150, "xdrip", "reading-1", legacyId: "sent-legacy-id"), WriteOrigin.Live);

        written.Created.Should().BeFalse();
        AssertAsSent(written.Record, storedId, 150, "sent-legacy-id");
        AssertAsSent(_published.Updated.Should().ContainSingle().Subject, storedId, 150, "sent-legacy-id");
        await using var owner = await OwnerAsync();
        var row = await owner.SensorGlucose.AsNoTracking().SingleAsync(e => e.SyncIdentifier == "reading-1");
        row.Mgdl.Should().Be(150);
        row.LegacyId.Should().Be("stored-legacy-id", "the row keeps the id it was first answered under");
        row.SysCreatedAt.Should().Be(StoredCreatedAt);
    }

    [Fact]
    public async Task SyncKey_BatchStoredBeforeTheWindow_IsUpdatedAndAnsweredAndBroadcastAsSent()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        var storedId = await SeedSensorGlucoseAsync(timestamp, "stored-legacy-id", "xdrip", "reading-2");
        await using var scope = ClampedScope();

        var written = await SensorGlucose(scope).BulkCreateAsync(
            [Reading(timestamp, 160, "xdrip", "reading-2", legacyId: null)], WriteOrigin.Live);

        AssertAsSent(written.Updated.Should().ContainSingle().Subject, storedId, 160, null);
        written.Created.Should().BeEmpty();
        AssertAsSent(_published.Updated.Should().ContainSingle().Subject, storedId, 160, null);
        await using var owner = await OwnerAsync();
        var row = await owner.SensorGlucose.AsNoTracking().SingleAsync(e => e.SyncIdentifier == "reading-2");
        row.Mgdl.Should().Be(160);
        row.LegacyId.Should().Be("stored-legacy-id");
    }

    [Fact]
    public async Task SyncKey_StoredInsideTheWindow_AnswersAsItAlwaysDid()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddHours(-1));
        await SeedSensorGlucoseAsync(timestamp, "stored-legacy-id", "xdrip", "reading-3");
        await using var scope = ClampedScope();

        var written = await SensorGlucose(scope).UpsertAsync(
            Reading(timestamp, 170, "xdrip", "reading-3", legacyId: "sent-legacy-id"), WriteOrigin.Live);

        written.Record.LegacyId.Should().Be("stored-legacy-id");
        written.Record.CreatedAt.Should().Be(StoredCreatedAt);
        _published.Updated.Should().ContainSingle().Which.LegacyId.Should().Be("stored-legacy-id");
    }

    [Fact]
    public async Task SyncKey_UserDeletedBeforeTheWindow_IsRefusedAsForAnUnclampedWriter()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        var storedId = await SeedSensorGlucoseAsync(timestamp, "stored-legacy-id", "xdrip", "reading-4");
        await using (var owner = await OwnerAsync())
        {
            await owner.SensorGlucose.Where(e => e.Id == storedId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.DeletedAt, DateTime.UtcNow.AddDays(-2))
                .SetProperty(e => EF.Property<bool>(e, NocturneDbContext.DeletedByUserProperty), true));
        }
        await using var scope = ClampedScope();

        await FluentActions
            .Awaiting(() => SensorGlucose(scope).UpsertAsync(
                Reading(timestamp, 180, "xdrip", "reading-4", legacyId: null), WriteOrigin.Live))
            .Should().ThrowAsync<RecreationBlockedException>();
    }

    [Fact]
    public async Task LegacyId_StoredBeforeTheWindow_IsUpdatedAndAnsweredBroadcastAndProjectedAsSent()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        var storedId = await SeedMeterGlucoseAsync(timestamp, 90, "meter-legacy-id");
        await using var scope = ClampedScope();

        var written = await MeterGlucose(scope).BulkUpsertAsync(
            [new MeterGlucose { Timestamp = timestamp, Mgdl = 120, LegacyId = "meter-legacy-id" }], WriteOrigin.Live);

        AssertAsSent(written.Updated.Should().ContainSingle().Subject, storedId, 120, "meter-legacy-id");
        AssertAsSent(_published.Updated.Should().ContainSingle().Subject, storedId, 120, "meter-legacy-id");
        var entry = _projected.Updated.Should().ContainSingle().Subject;
        entry.Mgdl.Should().Be(120);
        entry.SrvCreated.Should().NotBe(new DateTimeOffset(StoredCreatedAt).ToUnixTimeMilliseconds(), "the stored creation time is not the writer's to read");
        await using var owner = await OwnerAsync();
        var row = await owner.MeterGlucose.AsNoTracking().SingleAsync(e => e.LegacyId == "meter-legacy-id");
        row.Id.Should().Be(storedId);
        row.Mgdl.Should().Be(120);
    }

    [Fact]
    public async Task LegacyId_HiddenRowEqualToTheUpload_AnswersUpdatedNotUnchanged()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        await SeedMeterGlucoseAsync(timestamp, 90, "meter-same");
        MeterGlucose Upload() => new() { Timestamp = timestamp, Mgdl = 90, LegacyId = "meter-same" };

        await using (var unclamped = _provider.CreateAsyncScope())
        {
            TenantReaderData.Resolve(unclamped.ServiceProvider, _tenant);
            (await MeterGlucose(unclamped).BulkUpsertAsync([Upload()], WriteOrigin.Live)).Unchanged
                .Should().ContainSingle("the control: a writer who can read the row is told it matches");
        }

        await using var scope = ClampedScope();
        var written = await MeterGlucose(scope).BulkUpsertAsync([Upload()], WriteOrigin.Live);

        written.Unchanged.Should().BeEmpty("a clamped writer is not told whether a guess equals a row it cannot read");
        written.Updated.Should().ContainSingle().Which.CreatedAt.Should().NotBe(StoredCreatedAt);
    }

    [Fact]
    public async Task LegacyId_BulkCreateOfAnIdHeldBeforeTheWindow_IsSkippedAsForAnUnclampedWriter()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        await SeedMeterGlucoseAsync(timestamp, 90, "meter-held");
        await using var scope = ClampedScope();

        var written = await MeterGlucose(scope).BulkCreateAsync(
            [new MeterGlucose { Timestamp = timestamp, Mgdl = 130, LegacyId = "meter-held" }], WriteOrigin.Live);

        written.Should().BeEmpty();
        written.SkippedDeleted.Should().Be(0);
        _published.Updated.Should().BeEmpty();
        await using var owner = await OwnerAsync();
        (await owner.MeterGlucose.AsNoTracking().SingleAsync(e => e.LegacyId == "meter-held")).Mgdl.Should().Be(90);
    }

    [Fact]
    public async Task LegacyId_CreateOfAnIdHeldBeforeTheWindow_IsRefusedAsForAnUnclampedWriter()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        await SeedMeterGlucoseAsync(timestamp, 90, "meter-single");
        await using var scope = ClampedScope();

        await FluentActions
            .Awaiting(() => MeterGlucose(scope).CreateAsync(
                new MeterGlucose { Timestamp = timestamp, Mgdl = 130, LegacyId = "meter-single" }, WriteOrigin.Live))
            .Should().ThrowAsync<RecreationBlockedException>();
    }

    [Fact]
    public async Task LegacyId_HiddenRow_KeepsItsCorrelationIdAndAnswersTheSentOne()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        var storedCorrelation = Guid.CreateVersion7();
        var sentCorrelation = Guid.CreateVersion7();
        await SeedMeterGlucoseAsync(timestamp, 90, "meter-group", correlationId: storedCorrelation);
        await using var scope = ClampedScope();
        using var kept = KeptCorrelationIds.Open();

        var written = await MeterGlucose(scope).BulkUpsertAsync(
            [new MeterGlucose { Timestamp = timestamp, Mgdl = 95, LegacyId = "meter-group", CorrelationId = sentCorrelation }],
            WriteOrigin.Live);

        written.Updated.Should().ContainSingle().Which.CorrelationId.Should().Be(sentCorrelation);
        _published.Updated.Should().ContainSingle().Which.CorrelationId.Should().Be(sentCorrelation);
        kept.Resolve(sentCorrelation).Should().Be(storedCorrelation, "the decomposer writes the rest of the group under it");
        await using var owner = await OwnerAsync();
        var row = await owner.MeterGlucose.AsNoTracking().SingleAsync(e => e.LegacyId == "meter-group");
        row.CorrelationId.Should().Be(storedCorrelation, "a fresh id from a clamped lookup would move the row out of its group");
        row.Mgdl.Should().Be(95);
    }

    [Fact]
    public async Task LegacyId_VisibleRow_TakesTheSentCorrelationIdAsItAlwaysDid()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddHours(-1));
        var sentCorrelation = Guid.CreateVersion7();
        await SeedMeterGlucoseAsync(timestamp, 90, "meter-visible", correlationId: Guid.CreateVersion7());
        await using var scope = ClampedScope();
        using var kept = KeptCorrelationIds.Open();

        await MeterGlucose(scope).BulkUpsertAsync(
            [new MeterGlucose { Timestamp = timestamp, Mgdl = 95, LegacyId = "meter-visible", CorrelationId = sentCorrelation }],
            WriteOrigin.Live);

        kept.Resolve(sentCorrelation).Should().Be(sentCorrelation);
        await using var owner = await OwnerAsync();
        (await owner.MeterGlucose.AsNoTracking().SingleAsync(e => e.LegacyId == "meter-visible"))
            .CorrelationId.Should().Be(sentCorrelation);
    }

    [Fact]
    public async Task SyncKey_HiddenRow_KeepsItsCorrelationIdOnTheSingleAndTheBatchPath()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        var single = Guid.CreateVersion7();
        var batch = Guid.CreateVersion7();
        await SeedSensorGlucoseAsync(timestamp, "single-legacy-id", "xdrip", "reading-corr-1", correlationId: single);
        await SeedSensorGlucoseAsync(timestamp.AddMinutes(5), "batch-legacy-id", "xdrip", "reading-corr-2", correlationId: batch);
        await using var scope = ClampedScope();
        var sent = Guid.CreateVersion7();

        var upserted = await SensorGlucose(scope).UpsertAsync(
            Reading(timestamp, 150, "xdrip", "reading-corr-1", legacyId: null, correlationId: sent), WriteOrigin.Live);
        var bulk = await SensorGlucose(scope).BulkCreateAsync(
            [Reading(timestamp.AddMinutes(5), 155, "xdrip", "reading-corr-2", legacyId: null, correlationId: sent)],
            WriteOrigin.Live);

        upserted.Record.CorrelationId.Should().Be(sent);
        bulk.Updated.Should().ContainSingle().Which.CorrelationId.Should().Be(sent);
        await using var owner = await OwnerAsync();
        (await owner.SensorGlucose.AsNoTracking().SingleAsync(e => e.SyncIdentifier == "reading-corr-1"))
            .CorrelationId.Should().Be(single);
        (await owner.SensorGlucose.AsNoTracking().SingleAsync(e => e.SyncIdentifier == "reading-corr-2"))
            .CorrelationId.Should().Be(batch);
    }

    [Fact]
    public async Task SyncKey_UpsertTargets_HoldTheStoredTimeOfAHiddenRowTheWriteMoves()
    {
        var stored = Truncated(DateTime.UtcNow.AddDays(-3));
        await SeedSensorGlucoseAsync(stored, "moved-legacy-id", "xdrip", "reading-moved");
        await using var scope = ClampedScope();

        var targets = await SensorGlucose(scope).GetUpsertTargetTimestampsAsync(
            [Reading(stored.AddDays(1), 150, "xdrip", "reading-moved", legacyId: null)]);

        targets.Should().Equal([stored], "the day the row leaves has cached reads to clear");
    }

    [Fact]
    public async Task SyncKey_RecreationCheck_SeesAUserDeleteBeforeTheWindow()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        var storedId = await SeedSensorGlucoseAsync(timestamp, "deleted-legacy-id", "xdrip", "reading-deleted");
        await DeleteByUserAsync(storedId);
        await using var scope = ClampedScope();

        (await SensorGlucose(scope).IsRecreationBlockedAsync("xdrip", "reading-deleted"))
            .Should().BeTrue("the guard refuses what the lifted upsert it guards refuses");
    }

    [Fact]
    public async Task LegacyId_HiddenRowsPatientDevice_IsKeptButNotAnswered()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        var patientDevice = await SeedPatientDeviceAsync();
        var storedId = await SeedMeterGlucoseAsync(timestamp, 90, "meter-attributed", patientDeviceId: patientDevice);
        await using var scope = ClampedScope();

        var written = await MeterGlucose(scope).BulkUpsertAsync(
            [new MeterGlucose { Timestamp = timestamp, Mgdl = 100, LegacyId = "meter-attributed" }], WriteOrigin.Live);

        var answered = written.Updated.Should().ContainSingle().Subject;
        answered.Id.Should().Be(storedId);
        answered.PatientDeviceId.Should().BeNull("the stored attribution is not the writer's to read");
        _published.Updated.Should().ContainSingle().Which.Should().BeOfType<MeterGlucose>()
            .Which.PatientDeviceId.Should().BeNull();
        await using var owner = await OwnerAsync();
        (await owner.MeterGlucose.AsNoTracking().SingleAsync(e => e.Id == storedId))
            .PatientDeviceId.Should().Be(patientDevice, "a re-send that resolved no device keeps the stored one");
    }

    [Fact]
    public async Task CorrelationId_HeldByAHiddenExtrasRowOrReport_HoldsOffTheReSend()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        var correlationId = Guid.CreateVersion7();
        await using (var owner = await OwnerAsync())
        {
            owner.DeviceStatusExtras.Add(new DeviceStatusExtrasEntity
            {
                Id = Guid.CreateVersion7(), CorrelationId = correlationId, Timestamp = timestamp, ExtrasJson = "{\"a\":1}",
            });
            owner.SensorStateReports.Add(new SensorStateReportEntity
            {
                Id = Guid.CreateVersion7(), CorrelationId = correlationId, Timestamp = timestamp,
                State = nameof(SensorState.Ok), ReportedField = SensorStateReport.XDripJsField, ReportedValue = "6",
            });
            await owner.SaveChangesAsync();
        }
        await using var scope = ClampedScope();

        var extras = await new DeviceStatusExtrasRepository(
                Factory(scope), Mock.Of<IAuditContext>(), NullLogger<DeviceStatusExtrasRepository>.Instance)
            .BulkCreateAsync(
                [new DeviceStatusExtras { CorrelationId = correlationId, Timestamp = timestamp, Extras = new() { ["a"] = 2 } }],
                WriteOrigin.Live);
        var reports = await new SensorStateReportRepository(
                Factory(scope), Mock.Of<IAuditContext>(), NullLogger<SensorStateReportRepository>.Instance, _published)
            .BulkCreateAsync(
                [new SensorStateReport
                {
                    CorrelationId = correlationId, Timestamp = timestamp, State = SensorState.Ok,
                    ReportedField = SensorStateReport.XDripJsField, ReportedValue = "6",
                }],
                WriteOrigin.Live);

        extras.Should().BeEmpty();
        reports.Should().BeEmpty();
        await using var check = await OwnerAsync();
        (await check.DeviceStatusExtras.CountAsync(e => e.CorrelationId == correlationId)).Should().Be(1);
        (await check.SensorStateReports.CountAsync(e => e.CorrelationId == correlationId)).Should().Be(1);
    }

    private static void AssertAsSent(IV4Record answered, Guid storedId, double mgdl, string? legacyId)
    {
        answered.Id.Should().Be(storedId);
        answered.LegacyId.Should().Be(legacyId, "the stored legacy id is not the writer's to read");
        answered.CreatedAt.Should().NotBe(StoredCreatedAt, "the stored creation time is not the writer's to read");
        var answeredMgdl = answered switch
        {
            SensorGlucose reading => reading.Mgdl,
            MeterGlucose reading => reading.Mgdl,
            _ => double.NaN,
        };
        answeredMgdl.Should().Be(mgdl);
    }

    private static SensorGlucose Reading(
        DateTime timestamp, double mgdl, string dataSource, string syncIdentifier, string? legacyId,
        Guid? correlationId = null) => new()
    {
        Timestamp = timestamp, Mgdl = mgdl, DataSource = dataSource, SyncIdentifier = syncIdentifier, LegacyId = legacyId,
        CorrelationId = correlationId,
    };

    private async Task<Guid> SeedSensorGlucoseAsync(
        DateTime timestamp, string legacyId, string dataSource, string syncIdentifier, Guid? correlationId = null)
    {
        var id = Guid.CreateVersion7();
        await using var owner = await OwnerAsync();
        owner.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = id, Timestamp = timestamp, Mgdl = 100, LegacyId = legacyId, DataSource = dataSource,
            SyncIdentifier = syncIdentifier, CorrelationId = correlationId,
        });
        await owner.SaveChangesAsync();
        await owner.SensorGlucose.Where(e => e.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.SysCreatedAt, StoredCreatedAt));
        return id;
    }

    private async Task<Guid> SeedMeterGlucoseAsync(
        DateTime timestamp, double mgdl, string legacyId, Guid? correlationId = null, Guid? patientDeviceId = null)
    {
        var id = Guid.CreateVersion7();
        await using var owner = await OwnerAsync();
        owner.MeterGlucose.Add(new MeterGlucoseEntity
        {
            Id = id, Timestamp = timestamp, Mgdl = mgdl, LegacyId = legacyId, CorrelationId = correlationId,
            PatientDeviceId = patientDeviceId,
        });
        await owner.SaveChangesAsync();
        await owner.MeterGlucose.Where(e => e.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.SysCreatedAt, StoredCreatedAt));
        return id;
    }

    private async Task<Guid> SeedPatientDeviceAsync()
    {
        var id = Guid.CreateVersion7();
        await using var owner = await OwnerAsync();
        owner.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = id, DeviceCategory = "GlucoseMeter", Manufacturer = "Test", Model = "Meter",
        });
        await owner.SaveChangesAsync();
        return id;
    }

    private async Task DeleteByUserAsync(Guid sensorGlucoseId)
    {
        await using var owner = await OwnerAsync();
        await owner.SensorGlucose.Where(e => e.Id == sensorGlucoseId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.DeletedAt, DateTime.UtcNow.AddDays(-2))
            .SetProperty(e => EF.Property<bool>(e, NocturneDbContext.DeletedByUserProperty), true));
    }

    private static DateTime Truncated(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private SensorGlucoseRepository SensorGlucose(AsyncServiceScope scope) => new(
        Factory(scope), Mock.Of<IDuplicateLinker>(), Mock.Of<IAuditContext>(),
        NullLogger<SensorGlucoseRepository>.Instance, _published, _projected);

    private MeterGlucoseRepository MeterGlucose(AsyncServiceScope scope) => new(
        Factory(scope), Mock.Of<IAuditContext>(), NullLogger<MeterGlucoseRepository>.Instance, _published, _projected);

    private ValueTask<NocturneDbContext> OwnerAsync() =>
        _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);

    private AsyncServiceScope ClampedScope()
    {
        var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        return scope;
    }

    private static INocturneDbContextFactory Factory(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();

    private sealed class CapturingPublisher : IRecordChangePublisher
    {
        public List<IV4Record> Updated { get; } = [];

        public Task PublishAsync(RecordsChanged batch, CancellationToken ct)
        {
            Updated.AddRange(batch.Changes.SelectMany(c => c.Updated));
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingEntrySink : IDataEventSink<Entry>
    {
        public List<Entry> Updated { get; } = [];

        public Task OnUpdatedAsync(Entry item, CancellationToken ct = default)
        {
            Updated.Add(item);
            return Task.CompletedTask;
        }
    }
}
