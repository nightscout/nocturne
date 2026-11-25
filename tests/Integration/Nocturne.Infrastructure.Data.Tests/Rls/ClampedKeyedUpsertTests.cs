using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.Health;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A history-clamped writer re-sending a keyed record stored before its window updates that record as an
/// unclamped writer does. The unique index on the key spans the tenant, so an insert beside the row the
/// clamp hides would fail with 23505. The answer carries nothing the writer did not send.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ClampedKeyedUpsertTests(RlsCompletenessFixture fx) : IAsyncLifetime
{
    private static readonly DateTime StoredCreatedAt = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task SystemEvent_StoredBeforeTheWindow_IsUpdatedInPlaceAndAnsweredAsSent()
    {
        var mills = new DateTimeOffset(DateTime.UtcNow.AddDays(-2)).ToUnixTimeMilliseconds();
        await SeedAsync(db => db.SystemEvents.Add(new SystemEventEntity
        {
            Id = Guid.CreateVersion7(), EventType = "Info", Category = "Pump", Description = "stored", Mills = mills,
            OriginalId = "event-hidden", CreatedAt = StoredCreatedAt,
        }));
        await using var scope = ClampedScope();
        var repository = new SystemEventRepository(Factory(scope));

        var answered = await repository.UpsertSystemEventAsync(new SystemEvent
        {
            EventType = SystemEventType.Alarm, Category = SystemEventCategory.Cgm, Description = "resent", Mills = mills,
            Source = "manual", OriginalId = "event-hidden",
        });

        answered.Id.Should().Be("event-hidden");
        answered.Description.Should().Be("resent");
        answered.CreatedAt.Should().BeNull("the stored creation time is not the writer's to read");
        await using var owner = await OwnerAsync();
        var row = await owner.SystemEvents.AsNoTracking().SingleAsync(e => e.OriginalId == "event-hidden");
        row.Description.Should().Be("resent");
        row.EventType.Should().Be("Alarm");
        row.CreatedAt.Should().Be(StoredCreatedAt);
    }

    [Fact]
    public async Task SystemEvent_StoredInsideTheWindow_AnswersAsItAlwaysDid()
    {
        var mills = new DateTimeOffset(DateTime.UtcNow.AddHours(-1)).ToUnixTimeMilliseconds();
        await SeedAsync(db => db.SystemEvents.Add(new SystemEventEntity
        {
            Id = Guid.CreateVersion7(), EventType = "Info", Category = "Pump", Mills = mills,
            OriginalId = "event-visible", CreatedAt = StoredCreatedAt,
        }));
        await using var scope = ClampedScope();
        var repository = new SystemEventRepository(Factory(scope));

        var answered = await repository.UpsertSystemEventAsync(new SystemEvent
        {
            EventType = SystemEventType.Info, Category = SystemEventCategory.Pump, Description = "resent", Mills = mills,
            OriginalId = "event-visible",
        });

        answered.CreatedAt.Should().Be(StoredCreatedAt);
        await using var owner = await OwnerAsync();
        (await owner.SystemEvents.CountAsync(e => e.OriginalId == "event-visible")).Should().Be(1);
    }

    [Fact]
    public async Task SystemEvent_NewKey_InsertsForAClampedWriter()
    {
        await using var scope = ClampedScope();
        var repository = new SystemEventRepository(Factory(scope));

        await repository.UpsertSystemEventAsync(new SystemEvent
        {
            EventType = SystemEventType.Info, Category = SystemEventCategory.Pump,
            Mills = new DateTimeOffset(DateTime.UtcNow.AddDays(-4)).ToUnixTimeMilliseconds(), OriginalId = "event-new",
        });

        await using var owner = await OwnerAsync();
        (await owner.SystemEvents.CountAsync(e => e.OriginalId == "event-new")).Should().Be(1);
    }

    [Fact]
    public async Task StateSpan_StoredBeforeTheWindow_IsUpdatedInPlaceAndAnsweredAsSent()
    {
        var now = DateTime.UtcNow;
        var successor = Guid.CreateVersion7();
        await SeedAsync(db => db.StateSpans.AddRange(
            new StateSpanEntity
            {
                Id = Guid.CreateVersion7(), Category = "Override", State = "stored", LegacyId = "span-hidden",
                StartTimestamp = now.AddDays(-5), EndTimestamp = now.AddDays(-3), SupersededById = successor,
            },
            new StateSpanEntity
            {
                Id = successor, Category = "Override", State = "open", StartTimestamp = now.AddDays(-3),
            }));
        DateTime storedCreatedAt;
        await using (var before = await OwnerAsync())
            storedCreatedAt = (await before.StateSpans.AsNoTracking().SingleAsync(s => s.LegacyId == "span-hidden")).CreatedAt;
        await using var scope = ClampedScope();
        var repository = StateSpans(scope);

        var answered = await repository.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.Override, State = "resent", StartTimestamp = now.AddDays(-5),
            OriginalId = "span-hidden",
        });

        answered.Id.Should().Be("span-hidden");
        answered.State.Should().Be("resent");
        answered.SupersededById.Should().BeNull("the row's supersession is not the writer's to read");
        answered.EndTimestamp.Should().BeNull("the stored end is not the writer's to read");
        answered.CreatedAt.Should().NotBe(storedCreatedAt);
        await using var owner = await OwnerAsync();
        var row = await owner.StateSpans.AsNoTracking().SingleAsync(s => s.LegacyId == "span-hidden");
        row.State.Should().Be("resent");
        row.SupersededById.Should().Be(successor, "the upload named no end, so the supersession stands");
        row.CreatedAt.Should().Be(storedCreatedAt);
    }

    [Fact]
    public async Task StateSpan_UserDeletedBeforeTheWindow_IsNotRecreatedOrRevealed()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(db =>
        {
            var tombstone = new StateSpanEntity
            {
                Id = Guid.CreateVersion7(), Category = "Override", State = "deleted state", LegacyId = "span-deleted",
                StartTimestamp = now.AddDays(-5), EndTimestamp = now.AddDays(-4), DeletedAt = now.AddDays(-4),
            };
            db.StateSpans.Add(tombstone);
            db.Entry(tombstone).Property(NocturneDbContext.DeletedByUserProperty).CurrentValue = true;
        });
        await using var scope = ClampedScope();
        var repository = StateSpans(scope);

        var answered = (await repository.BulkUpsertAsync(
        [
            new StateSpan
            {
                Category = StateSpanCategory.Override, State = "resent", StartTimestamp = now.AddDays(-5),
                OriginalId = "span-deleted",
            },
        ])).Single();

        answered.Outcome.Should().BeNull();
        answered.Span.State.Should().Be("resent", "the answer is the span as sent");
        await using var owner = await OwnerAsync();
        var rows = await owner.StateSpans.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.LegacyId == "span-deleted").ToListAsync();
        rows.Should().ContainSingle().Which.State.Should().Be("deleted state");
    }

    [Fact]
    public async Task BodyWeight_StoredBeforeTheWindow_IsUpdatedInPlaceAndAnsweredAsSent()
    {
        var mills = new DateTimeOffset(DateTime.UtcNow.AddDays(-3)).ToUnixTimeMilliseconds();
        await SeedAsync(db => db.BodyWeights.Add(new BodyWeightEntity
        {
            Id = Guid.CreateVersion7(), Mills = mills, WeightKg = 70, DataSource = "scale", SyncIdentifier = "weigh-in",
            CreatedAt = "stored creation time",
        }));
        await using var scope = ClampedScope();
        var repository = new BodyWeightRepository(Factory(scope), TimeProvider.System);

        var answered = await repository.UpsertAsync(
        [
            new BodyWeight { Mills = mills, WeightKg = 71, DataSource = "scale", SyncIdentifier = "weigh-in" },
        ]);

        answered.Should().ContainSingle().Which.WeightKg.Should().Be(71);
        answered[0].CreatedAt.Should().BeNull("the stored creation time is not the writer's to read");
        await using var owner = await OwnerAsync();
        var row = await owner.BodyWeights.AsNoTracking().SingleAsync(w => w.SyncIdentifier == "weigh-in");
        row.WeightKg.Should().Be(71);
        row.CreatedAt.Should().Be("stored creation time");
    }

    [Fact]
    public async Task HeartRate_StoredBeforeTheWindow_IsUpdatedInPlace()
    {
        var timestamp = DateTime.UtcNow.AddDays(-3);
        await SeedAsync(db => db.HeartRates.Add(new HeartRateEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = timestamp, Bpm = 60, DataSource = "watch", SyncIdentifier = "beat",
        }));
        await using var scope = ClampedScope();
        var repository = new HeartRateRepository(Factory(scope), TimeProvider.System);

        var answered = await repository.UpsertAsync(
        [
            new HeartRate { Timestamp = timestamp, Bpm = 90, DataSource = "watch", SyncIdentifier = "beat" },
        ]);

        answered.Should().ContainSingle().Which.Bpm.Should().Be(90);
        await using var owner = await OwnerAsync();
        (await owner.HeartRates.AsNoTracking().SingleAsync(h => h.SyncIdentifier == "beat")).Bpm.Should().Be(90);
    }

    [Fact]
    public async Task ActivityHealthSeries_StoredBeforeTheWindow_IsUpdatedInPlace()
    {
        var timestamp = DateTime.UtcNow.AddDays(-3);
        await SeedAsync(db => db.StepCounts.Add(new StepCountEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = timestamp, Metric = 100, DataSource = "watch", SyncIdentifier = "stride",
        }));
        await using var scope = ClampedScope();
        var repository = new ActivityRecordRepository(Factory(scope), TimeProvider.System);

        var written = await repository.WriteHealthSeriesAsync(
            [], [(0, new StepCount { Timestamp = timestamp, Metric = 250, DataSource = "watch", SyncIdentifier = "stride" })]);

        written.StepCountWrites.Updated.Should().ContainSingle();
        await using var owner = await OwnerAsync();
        (await owner.StepCounts.AsNoTracking().SingleAsync(s => s.SyncIdentifier == "stride")).Metric.Should().Be(250);
    }

    [Fact]
    public async Task SleepSession_StoredBeforeTheWindow_IsReplacedInPlaceAndAnsweredAsSent()
    {
        var start = DateTime.UtcNow.AddDays(-7);
        Guid storedId;
        await using (var unclamped = _provider.CreateAsyncScope())
        {
            TenantReaderData.Resolve(unclamped.ServiceProvider, _tenant);
            storedId = Guid.Parse((await new SleepSessionRepository(Factory(unclamped)).UpsertSessionAsync(
                Session(start, "sleep-hidden", createdAt: StoredCreatedAt))).Id!);
        }

        await using var scope = ClampedScope();
        var answered = await new SleepSessionRepository(Factory(scope)).UpsertSessionAsync(
            Session(start, "sleep-hidden", createdAt: null));

        answered.Id.Should().Be(storedId.ToString(), "a re-sync keeps the session's id");
        answered.CreatedAt.Should().NotBe(StoredCreatedAt, "the stored creation time is not the writer's to read");
        await using var read = await OwnerAsync();
        var row = await read.SleepSessions.AsNoTracking().SingleAsync(s => s.OriginalId == "sleep-hidden");
        row.Id.Should().Be(storedId);
        row.CreatedAt.Should().Be(StoredCreatedAt);
    }

    [Fact]
    public async Task SleepSession_UserDeletedBeforeTheWindow_IsRefusedWithoutNamingTheStoredRow()
    {
        var start = DateTime.UtcNow.AddDays(-7);
        var storedId = Guid.CreateVersion7();
        await SeedAsync(db =>
        {
            var tombstone = new SleepSessionEntity
            {
                Id = storedId, TenantId = _tenant, OriginalId = "stored-original-id", Source = "StoredSource",
                StartTime = start, EndTime = start.AddHours(8), Type = "Overnight", DetectionMethod = "Auto",
                DeletedAt = DateTime.UtcNow.AddDays(-6),
            };
            db.SleepSessions.Add(tombstone);
            db.Entry(tombstone).Property(NocturneDbContext.DeletedByUserProperty).CurrentValue = true;
        });
        await using var scope = ClampedScope();
        var request = Session(start, "request-original-id", createdAt: null);
        request.Id = storedId.ToString();

        var refusal = (await FluentActions
            .Awaiting(() => new SleepSessionRepository(Factory(scope)).UpsertSessionAsync(request))
            .Should().ThrowAsync<RecreationBlockedException>()).Which;

        refusal.Message.Should().Contain("request-original-id").And.Contain("Fitbit");
        refusal.Message.Should().NotContain("stored-original-id").And.NotContain("StoredSource");
        await using var owner = await OwnerAsync();
        var rows = await owner.SleepSessions.IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == storedId).ToListAsync();
        rows.Should().ContainSingle().Which.OriginalId.Should().Be("stored-original-id");
    }

    [Fact]
    public async Task StateSpan_HiddenRowEqualToTheUpload_AnswersUpdatedNotUnchanged()
    {
        var start = Truncated(DateTime.UtcNow.AddDays(-5));
        var end = Truncated(DateTime.UtcNow.AddDays(-3));
        await SeedAsync(db => db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), Category = "Override", State = "same", LegacyId = "span-same",
            StartTimestamp = start, EndTimestamp = end,
        }));
        StateSpan Upload() => new()
        {
            Category = StateSpanCategory.Override, State = "same", StartTimestamp = start, EndTimestamp = end,
            OriginalId = "span-same",
        };

        await using (var unclamped = _provider.CreateAsyncScope())
        {
            TenantReaderData.Resolve(unclamped.ServiceProvider, _tenant);
            (await StateSpans(unclamped).BulkUpsertAsync([Upload()])).Single().Outcome
                .Should().Be(UpsertOutcome.Unchanged, "the control: a writer who can read the row is told it matches");
        }

        await using var scope = ClampedScope();
        (await StateSpans(scope).BulkUpsertAsync([Upload()])).Single().Outcome
            .Should().Be(UpsertOutcome.Updated, "a clamped writer is not told whether a guess equals a row it cannot read");
    }

    [Fact]
    public async Task ActivityHealthSeries_HiddenRowEqualToTheUpload_AnswersUpdatedNotUnchanged()
    {
        var timestamp = Truncated(DateTime.UtcNow.AddDays(-3));
        await SeedAsync(db => db.StepCounts.Add(new StepCountEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = timestamp, Metric = 100, DataSource = "watch", SyncIdentifier = "stride-same",
        }));
        (int, StepCount) Upload() =>
            (0, new StepCount { Timestamp = timestamp, Metric = 100, DataSource = "watch", SyncIdentifier = "stride-same" });

        await using (var unclamped = _provider.CreateAsyncScope())
        {
            TenantReaderData.Resolve(unclamped.ServiceProvider, _tenant);
            var control = await new ActivityRecordRepository(Factory(unclamped), TimeProvider.System)
                .WriteHealthSeriesAsync([], [Upload()]);
            control.StepCountWrites.Unchanged.Should().ContainSingle("the control: a writer who can read the row is told it matches");
        }

        await using var scope = ClampedScope();
        var written = await new ActivityRecordRepository(Factory(scope), TimeProvider.System)
            .WriteHealthSeriesAsync([], [Upload()]);

        written.StepCountWrites.Unchanged.Should().BeEmpty();
        written.StepCountWrites.Updated.Should().ContainSingle();
    }

    [Fact]
    public async Task HeartRate_ResentByAnIdStoredBeforeTheWindow_IsUpdatedInPlace()
    {
        var timestamp = DateTime.UtcNow.AddDays(-3);
        const string recordId = "glooko-heart-rate-1";
        await SeedAsync(db => db.HeartRates.Add(new HeartRateEntity
        {
            Id = RecordIdKey.ParseIdToGuid(recordId), Timestamp = timestamp, Bpm = 60,
        }));
        await using var scope = ClampedScope();
        var repository = new HeartRateRepository(Factory(scope), TimeProvider.System);

        var answered = await repository.UpsertAsync([new HeartRate { Id = recordId, Timestamp = timestamp, Bpm = 90 }]);

        answered.Should().ContainSingle().Which.Bpm.Should().Be(90);
        await using var owner = await OwnerAsync();
        var rows = await owner.HeartRates.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle().Which.Bpm.Should().Be(90);
    }

    private static DateTime Truncated(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static SleepSession Session(DateTime start, string originalId, DateTime? createdAt) => new()
    {
        StartTime = start, EndTime = start.AddHours(8), Type = SleepSessionType.Overnight,
        DetectionMethod = SleepDetectionMethod.Auto, Source = SleepSource.Fitbit, OriginalId = originalId,
        DurationMs = 28_800_000, TotalSleepMs = 25_200_000, CreatedAt = createdAt,
    };

    private static StateSpanRepository StateSpans(AsyncServiceScope scope) => new(
        Factory(scope), new Mock<IDuplicateLinker>().Object, new Mock<IAuditContext>().Object,
        NullLogger<StateSpanRepository>.Instance);

    private async Task SeedAsync(Action<NocturneDbContext> add)
    {
        await using var owner = await OwnerAsync();
        add(owner);
        await owner.SaveChangesAsync();
    }

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
}
