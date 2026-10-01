using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
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
        : TrackerRepository(context)
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
            UserId = OwnerId,
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
                UserId = OwnerId,
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
            ct => repository.StartInstanceAsync(
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
            var won = await new TrackerRepository(first).CompleteInstanceAsync(
                instanceId, CompletionReason.ReplacedEarly, "first", completedAt: Now.AddHours(-1));
            won.Should().NotBeNull();
            won!.CompletionReason.Should().Be(CompletionReason.ReplacedEarly);
        }

        await using (var second = Context(tenant))
        {
            var lost = await new TrackerRepository(second).CompleteInstanceAsync(
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
            Task.Run(() => StartAsync(new TrackerRepository(first), definition, Now, Now.AddDays(7))),
            Task.Run(() => StartAsync(new TrackerRepository(second), definition, Now, Now.AddDays(14))));

        results.Should().OnlyContain(r => r.Outcome == TrackerSuccessionOutcome.Started);
        (await RunningAsync(tenant, definition.Id)).Should().HaveCount(2);
    }
}
