using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

/// <summary>
/// On PostgreSQL under the runtime role and Row Level Security, two writers starting a run of one
/// Duration tracker at once leave exactly one run going (<see cref="TrackerSuccession"/>), and a
/// completion that loses to another changes nothing.
/// </summary>
[Trait("Category", "Integration")]
public class TrackerSuccessionPostgresTests(TrackerSuccessionPostgresTests.Database database)
    : IClassFixture<TrackerSuccessionPostgresTests.Database>
{
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);
    private const string OwnerId = "owner";

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("tracker_succession");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Holds each writer after it reads the running instances until the other has read them too, or
    /// a timeout passes. Unserialised writers therefore always both read before either writes; a
    /// writer holding the definition's lock waits out the timeout alone.
    /// </summary>
    private sealed class Rendezvous
    {
        private int _arrived;
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) == 2)
                _all.TrySetResult();
            return Task.WhenAny(_all.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
        }
    }

    private sealed class MeetAfterReadRepository(NocturneDbContext context, Rendezvous rendezvous)
        : TrackerRepository(context.AsFactory())
    {
        public override async Task<List<TrackerInstanceEntity>> GetActiveInstancesForDefinitionAsync(
            Guid definitionId, CancellationToken cancellationToken = default)
        {
            var running = await base.GetActiveInstancesForDefinitionAsync(definitionId, cancellationToken);
            await rendezvous.ArriveAsync();
            return running;
        }
    }

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString, o => o.EnableRetryOnFailure())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'tracker-succession-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"succession-{tenant:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }

    private async Task<TrackerDefinitionEntity> SeedDefinitionAsync(Guid tenant, TrackerMode mode, DateTime? runningSince)
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            CreatedBy = OwnerId,
            Name = "Infusion Site",
            Mode = mode,
            LifespanHours = mode == TrackerMode.Duration ? 72 : null,
        };
        await using var seed = Context(tenant);
        seed.TrackerDefinitions.Add(definition);
        if (runningSince is { } startedAt)
        {
            seed.TrackerInstances.Add(new TrackerInstanceEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                CreatedBy = OwnerId,
                DefinitionId = definition.Id,
                StartedAt = startedAt,
            });
        }
        await seed.SaveChangesAsync();
        return definition;
    }

    private static Task<TrackerSuccessionResult> StartAsync(
        TrackerRepository repository, TrackerDefinitionEntity definition, DateTime startedAt, DateTime? scheduledAt = null) =>
        TrackerSuccession.StartAsync(
            repository,
            Mock.Of<ISignalRBroadcastService>(),
            NullLogger.Instance,
            definition,
            startedAt,
            completionNotes: null,
            completeTreatmentId: null,
            (tx, ct) => tx.StartInstanceAsync(
                definition.Id, OwnerId, startedAt: startedAt, scheduledAt: scheduledAt, cancellationToken: ct),
            CancellationToken.None);

    private async Task<List<TrackerInstanceEntity>> RunningAsync(Guid tenant, Guid definitionId)
    {
        await using var db = Context(tenant);
        return await db.TrackerInstances.AsNoTracking()
            .Where(i => i.DefinitionId == definitionId && i.CompletedAt == null)
            .ToListAsync();
    }

    [Fact]
    public async Task CompletingAnAlreadyCompletedInstance_ChangesNothingAndReportsTheLoss()
    {
        var tenant = await SeedTenantAsync();
        var definition = await SeedDefinitionAsync(tenant, TrackerMode.Duration, Now.AddDays(-2));
        var instanceId = (await RunningAsync(tenant, definition.Id)).Single().Id;

        await using (var first = Context(tenant))
        {
            var won = await new TrackerRepository(first.AsFactory()).CompleteInstanceAsync(
                instanceId, CompletionReason.ReplacedEarly, "first", completedAt: Now.AddHours(-1));
            won.Should().NotBeNull();
            won!.CompletionReason.Should().Be(CompletionReason.ReplacedEarly);
        }

        await using (var second = Context(tenant))
        {
            var lost = await new TrackerRepository(second.AsFactory()).CompleteInstanceAsync(
                instanceId, CompletionReason.Failed, "second", "treatment", Now);
            lost.Should().BeNull();
        }

        await using var db = Context(tenant);
        var stored = await db.TrackerInstances.AsNoTracking().SingleAsync(i => i.Id == instanceId);
        stored.CompletionReason.Should().Be(CompletionReason.ReplacedEarly);
        stored.CompletionNotes.Should().Be("first");
        stored.CompleteTreatmentId.Should().BeNull();
        stored.CompletedAt.Should().Be(Now.AddHours(-1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentStartsOfADurationTracker_LeaveExactlyOneRunningInstance(bool alreadyRunning)
    {
        var tenant = await SeedTenantAsync();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var definition = await SeedDefinitionAsync(
                tenant, TrackerMode.Duration, alreadyRunning ? Now.AddDays(-2) : null);
            var rendezvous = new Rendezvous();
            await using var first = Context(tenant);
            await using var second = Context(tenant);

            var results = await Task.WhenAll(
                Task.Run(() => StartAsync(new MeetAfterReadRepository(first, rendezvous), definition, Now)),
                Task.Run(() => StartAsync(new MeetAfterReadRepository(second, rendezvous), definition, Now)));

            results.Count(r => r.Outcome == TrackerSuccessionOutcome.Started).Should().Be(1);
            var running = await RunningAsync(tenant, definition.Id);
            running.Should().ContainSingle().Which.StartedAt.Should().Be(Now);
        }
    }

    [Fact]
    public async Task ConcurrentStartsOfAnEventTracker_BothStayRunning()
    {
        var tenant = await SeedTenantAsync();
        var definition = await SeedDefinitionAsync(tenant, TrackerMode.Event, runningSince: null);
        await using var first = Context(tenant);
        await using var second = Context(tenant);

        var results = await Task.WhenAll(
            Task.Run(() => StartAsync(new TrackerRepository(first.AsFactory()), definition, Now, Now.AddDays(7))),
            Task.Run(() => StartAsync(new TrackerRepository(second.AsFactory()), definition, Now, Now.AddDays(14))));

        results.Should().OnlyContain(r => r.Outcome == TrackerSuccessionOutcome.Started);
        (await RunningAsync(tenant, definition.Id)).Should().HaveCount(2);
    }

    private static Task<TrackerSuccessionResult> StartFromEventAsync(
        TrackerRepository repository, TrackerDefinitionEntity definition, DateTime startedAt, string eventId) =>
        TrackerSuccession.StartAsync(
            repository,
            Mock.Of<ISignalRBroadcastService>(),
            NullLogger.Instance,
            definition,
            startedAt,
            completionNotes: "Auto-completed",
            completeTreatmentId: eventId,
            (tx, ct) => tx.StartInstanceAsync(
                definition.Id, OwnerId, startTreatmentId: eventId, startedAt: startedAt, cancellationToken: ct),
            CancellationToken.None);

    private static Task<TrackerStartRevert?> RevertAsync(
        TrackerRepository repository, TrackerDefinitionEntity definition, string eventId) =>
        repository.ExecuteUnderDefinitionLockAsync(
            definition.Id, (tx, ct) => tx.RevertStartAsync(definition.Id, eventId, ct));

    [Fact]
    public async Task RevertingTheRunningRunsStart_DeletesItAndResumesTheRunItReplaced()
    {
        var tenant = await SeedTenantAsync();
        var definition = await SeedDefinitionAsync(tenant, TrackerMode.Duration, Now.AddDays(-2));
        var replacedId = (await RunningAsync(tenant, definition.Id)).Single().Id;
        await using (var db = Context(tenant))
            await StartFromEventAsync(new TrackerRepository(db.AsFactory()), definition, Now, "change");

        TrackerStartRevert? revert;
        await using (var db = Context(tenant))
            revert = await RevertAsync(new TrackerRepository(db.AsFactory()), definition, "change");

        revert.Should().NotBeNull();
        revert!.Reopened.Should().ContainSingle().Which.Id.Should().Be(replacedId);
        var running = await RunningAsync(tenant, definition.Id);
        running.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = replacedId,
            CompletedAt = (DateTime?)null,
            CompletionReason = (CompletionReason?)null,
            CompletionNotes = (string?)null,
            CompleteTreatmentId = (string?)null,
        });
        await using var check = Context(tenant);
        (await check.TrackerInstances.CountAsync(i => i.DefinitionId == definition.Id)).Should().Be(1);
    }

    [Fact]
    public async Task RevertingAStartALaterChangeReplaced_ChangesNothing()
    {
        var tenant = await SeedTenantAsync();
        var definition = await SeedDefinitionAsync(tenant, TrackerMode.Duration, Now.AddDays(-2));
        await using (var db = Context(tenant))
        {
            var repository = new TrackerRepository(db.AsFactory());
            await StartFromEventAsync(repository, definition, Now.AddDays(-1), "first");
            await StartFromEventAsync(repository, definition, Now, "second");
        }

        TrackerStartRevert? revert;
        await using (var db = Context(tenant))
            revert = await RevertAsync(new TrackerRepository(db.AsFactory()), definition, "first");

        revert.Should().BeNull();
        (await RunningAsync(tenant, definition.Id)).Should().ContainSingle().Which.StartTreatmentId.Should().Be("second");
        await using var check = Context(tenant);
        (await check.TrackerInstances.CountAsync(i => i.DefinitionId == definition.Id)).Should().Be(3);
    }

    [Fact]
    public async Task RunningStartedBy_FindsOnlyTheRunningRunsTheIdsStarted()
    {
        var tenant = await SeedTenantAsync();
        var definition = await SeedDefinitionAsync(tenant, TrackerMode.Duration, Now.AddDays(-2));
        await using (var db = Context(tenant))
        {
            var repository = new TrackerRepository(db.AsFactory());
            await StartFromEventAsync(repository, definition, Now.AddDays(-1), "replaced");
            await StartFromEventAsync(repository, definition, Now, "running");
        }

        await using var read = Context(tenant);
        var found = await new TrackerRepository(read.AsFactory()).GetRunningStartedByAsync(["replaced", "running", "unknown"]);

        found.Should().ContainSingle().Which.Should().Match<TrackerInstanceEntity>(
            i => i.StartTreatmentId == "running" && i.Definition.Id == definition.Id);
    }

    /// <summary>
    /// The undone run was the tracker's first: nothing reopens, and a replayed change dated before
    /// it starts the tracker again, unless someone has since ended a run.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayAfterUndoingTheFirstRun_StartsTheTrackerUnlessARunEndedSince(bool endedSince)
    {
        var tenant = await SeedTenantAsync();
        var definition = await SeedDefinitionAsync(tenant, TrackerMode.Duration, runningSince: null);
        await using (var db = Context(tenant))
        {
            var repository = new TrackerRepository(db.AsFactory());
            await StartFromEventAsync(repository, definition, Now, "inferred");
            await RevertAsync(repository, definition, "inferred");
            if (endedSince)
            {
                var ended = await repository.StartInstanceAsync(definition.Id, OwnerId, startedAt: Now.AddHours(1));
                await repository.CompleteInstanceAsync(ended.Id, CompletionReason.Completed, completedAt: Now.AddHours(2));
            }
        }

        TrackerSuccessionResult result;
        await using (var db = Context(tenant))
        {
            var repository = new TrackerRepository(db.AsFactory());
            result = await TrackerSuccession.StartAsync(
                repository, Mock.Of<ISignalRBroadcastService>(), NullLogger.Instance, definition, Now.AddMinutes(-10),
                completionNotes: null, completeTreatmentId: "recorded",
                (tx, ct) => tx.StartInstanceAsync(
                    definition.Id, OwnerId, startTreatmentId: "recorded", startedAt: Now.AddMinutes(-10), cancellationToken: ct),
                CancellationToken.None, replay: true);
        }

        result.Outcome.Should().Be(endedSince ? TrackerSuccessionOutcome.EndedSince : TrackerSuccessionOutcome.Started);
    }

    [Fact]
    public async Task ABackdatedRun_FeedsTheAlertEngineItsStartAndTheTimeItWasRecorded()
    {
        var tenant = await SeedTenantAsync();
        var definition = await SeedDefinitionAsync(tenant, TrackerMode.Duration, runningSince: null);
        var clock = new FakeTimeProvider(Now);
        await using var db = Context(tenant);
        var repository = new TrackerRepository(db.AsFactory(), clock);

        await repository.StartInstanceAsync(definition.Id, OwnerId, startedAt: Now.AddDays(-3));
        var references = await repository.GetActiveTrackerReferencesAsync(new HashSet<Guid> { definition.Id }, Now);

        references[definition.Id].Should().Be(new ActiveTrackerReference(Now.AddDays(-3), Now, IsScheduled: false));
    }

    [Fact]
    public async Task ARunStartedNow_IsRecordedAtTheSameReadingAsItsStart()
    {
        var tenant = await SeedTenantAsync();
        var definition = await SeedDefinitionAsync(tenant, TrackerMode.Duration, runningSince: null);
        await using var db = Context(tenant);
        var repository = new TrackerRepository(db.AsFactory(), new FakeTimeProvider(Now));

        var run = await repository.StartInstanceAsync(definition.Id, OwnerId);

        run.StartedAt.Should().Be(Now);
        run.RecordedAt.Should().Be(Now);
    }

    [Fact]
    public async Task AnEventRun_ReachesTheAlertEngineAsScheduled()
    {
        var tenant = await SeedTenantAsync();
        var definition = await SeedDefinitionAsync(tenant, TrackerMode.Event, runningSince: null);
        await using var db = Context(tenant);
        var repository = new TrackerRepository(db.AsFactory(), new FakeTimeProvider(Now));

        await repository.StartInstanceAsync(definition.Id, OwnerId, startedAt: Now, scheduledAt: Now.AddHours(2));
        var references = await repository.GetActiveTrackerReferencesAsync(new HashSet<Guid> { definition.Id }, Now);

        references[definition.Id].Should().Be(new ActiveTrackerReference(Now.AddHours(2), Now, IsScheduled: true));
    }

    [Fact]
    public async Task UpdatingADefinition_StoresItsModeAndReservoirRuleAndTheClocksTime()
    {
        var tenant = await SeedTenantAsync();
        await using var db = Context(tenant);
        var repository = new TrackerRepository(db.AsFactory(), new FakeTimeProvider(Now));
        var definition = await repository.CreateDefinitionAsync(new TrackerDefinitionEntity
        {
            CreatedBy = OwnerId,
            Name = "Reservoir",
            Category = TrackerCategory.Reservoir,
            Mode = TrackerMode.Duration,
            LifespanHours = 72,
        });

        definition.Mode = TrackerMode.Event;
        definition.LifespanHours = null;
        definition.LowReservoirUnits = 20;
        definition.LowReservoirUrgency = NotificationUrgency.Hazard;
        await repository.UpdateDefinitionAsync(definition.Id, definition);

        var stored = await repository.GetDefinitionByIdAsync(definition.Id);
        stored!.Mode.Should().Be(TrackerMode.Event);
        stored.LowReservoirUnits.Should().Be(20);
        stored.LowReservoirUrgency.Should().Be(NotificationUrgency.Hazard);
        stored.CreatedAt.Should().Be(Now);
        stored.UpdatedAt.Should().Be(Now);
    }
}
