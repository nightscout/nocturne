using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Audit;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Two writes of one sleep session, the first held uncommitted until the second is blocked. The
/// second must wait on the first and then replace its row; without the session locks it misses the
/// uncommitted row, inserts, and fails a unique index once the first commits.
/// </summary>
[Trait("Category", "Integration")]
public class SleepSessionUpsertConcurrencyTests : ApiIntegrationTestBase
{
    public SleepSessionUpsertConcurrencyTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task UpsertSessionAsync_SameSourceRecordWhileFirstUncommitted_WaitsAndReplacesIt()
    {
        var (first, second) = await RaceAsync(
            repo => repo.UpsertSessionAsync(Session(id: null, originalId: "sleep-race-source", score: 70)),
            repo => repo.UpsertSessionAsync(Session(id: null, originalId: "sleep-race-source", score: 90)));

        second.Id.Should().Be(first!.Id);
        (await StoredAsync()).Should().ContainSingle()
            .Which.Should().Be((Guid.Parse(first.Id!), (string?)"sleep-race-source", (short?)90));
    }

    [Fact]
    public async Task UpsertSessionAsync_SameIdWithoutSourceRecordWhileFirstUncommitted_WaitsAndReplacesIt()
    {
        var id = Guid.CreateVersion7();

        var (first, second) = await RaceAsync(
            repo => repo.UpsertSessionAsync(Session(id: id, originalId: "sleep-race-by-id", score: 70)),
            repo => repo.UpsertSessionAsync(Session(id: id, originalId: null, score: 90)));

        first!.Id.Should().Be(id.ToString());
        second.Id.Should().Be(id.ToString());
        (await StoredAsync()).Should().ContainSingle().Which.Should().Be((id, (string?)null, (short?)90));
    }

    [Fact]
    public async Task UpsertSessionAsync_SourceRecordAnUncommittedUpdateIsMovingOntoARow_WaitsAndReplacesThatRow()
    {
        SleepSession stored;
        await using (var seed = Fixture.CreateDbContext(Fixture.TenantId))
        {
            stored = await Repository(new TestTenantDbContextFactory(seed))
                .UpsertSessionAsync(Session(id: null, originalId: "sleep-race-before-update", score: 60));
        }
        var storedId = Guid.Parse(stored.Id!);

        var (_, upserted) = await RaceAsync(
            repo => repo.UpdateSessionAsync(storedId, Session(id: null, originalId: "sleep-race-after-update", score: 70)),
            repo => repo.UpsertSessionAsync(Session(id: null, originalId: "sleep-race-after-update", score: 90)));

        upserted.Id.Should().Be(stored.Id);
        (await StoredAsync()).Should().ContainSingle()
            .Which.Should().Be((storedId, (string?)"sleep-race-after-update", (short?)90));
    }

    [Fact]
    public async Task DeleteSessionAsync_WhileAnUpsertOfThatIdIsUncommitted_WaitsAndDeletesTheReplacement()
    {
        var storedId = await SeedAsync("sleep-race-delete-after-upsert");

        var (_, deleted) = await RaceAsync(
            repo => repo.UpsertSessionAsync(Session(id: storedId, originalId: "sleep-race-delete-after-upsert", score: 90)),
            repo => repo.DeleteSessionAsync(storedId));

        deleted.Should().BeTrue();
        (await StoredAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertSessionAsync_WhileADeleteOfThatIdIsUncommitted_WaitsAndInsertsIt()
    {
        var storedId = await SeedAsync("sleep-race-upsert-after-delete");

        var (deleted, upserted) = await RaceAsync(
            repo => repo.DeleteSessionAsync(storedId),
            repo => repo.UpsertSessionAsync(Session(id: storedId, originalId: null, score: 90)));

        deleted.Should().BeTrue();
        upserted.Id.Should().Be(storedId.ToString());
        (await StoredAsync()).Should().ContainSingle().Which.Should().Be((storedId, (string?)null, (short?)90));
    }

    [Fact]
    public async Task UpsertSessionAsync_SourceRecordAnUncommittedUserDeleteIsTombstoning_WaitsAndIsRefused()
    {
        var storedId = await SeedAsync("sleep-race-upsert-after-user-delete");

        var race = () => RaceAsync(
            repo => repo.DeleteSessionAsync(storedId),
            repo => repo.UpsertSessionAsync(Session(id: null, originalId: "sleep-race-upsert-after-user-delete", score: 90)),
            firstAudit: new AuditContext { Endpoint = "DELETE /api/v1/activity" });

        await race.Should().ThrowExactlyAsync<RecreationBlockedException>();
        (await StoredAsync()).Should().BeEmpty();
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        (await db.SleepSessions.IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == Fixture.TenantId).ToListAsync())
            .Should().ContainSingle()
            .Which.Should().Match<Nocturne.Infrastructure.Data.Entities.SleepSessionEntity>(
                s => s.Id == storedId && s.DeletedAt != null && s.SleepScore == 60);
    }

    private async Task<Guid> SeedAsync(string originalId)
    {
        await using var seed = Fixture.CreateDbContext(Fixture.TenantId);
        var stored = await Repository(new TestTenantDbContextFactory(seed))
            .UpsertSessionAsync(Session(id: null, originalId: originalId, score: 60));
        return Guid.Parse(stored.Id!);
    }

    private async Task<(TFirst First, TSecond Second)> RaceAsync<TFirst, TSecond>(
        Func<SleepSessionRepository, Task<TFirst>> first,
        Func<SleepSessionRepository, Task<TSecond>> second,
        IAuditContext? firstAudit = null)
    {
        TestTenantDbContextFactory contexts;
        await using (var seed = Fixture.CreateDbContext(Fixture.TenantId))
            contexts = new TestTenantDbContextFactory(seed);
        var held = await contexts.CreateAsync();
        held.AuditContext = firstAudit;
        var racer = await contexts.CreateAsync();
        Task<TSecond>? secondTask = null;
        try
        {
            await racer.Database.OpenConnectionAsync();
            var racerPid = await racer.Database
                .SqlQuery<int>($"""SELECT pg_backend_pid() AS "Value" """)
                .SingleAsync();

            var firstResult = await held.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await held.Database.BeginTransactionAsync();
                var result = await first(Repository(new Pinned(held)));

                secondTask = second(Repository(new Pinned(racer)));
                await WaitUntilBlockedOnALockAsync(racerPid, secondTask);

                await transaction.CommitAsync();
                return result;
            });
            return (firstResult, await secondTask!.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            if (secondTask is not null)
            {
                try
                {
                    await secondTask.WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (Exception ex)
                {
                    Output.WriteLine($"Second write after the race: {ex}");
                }
            }
            await held.Database.CloseConnectionAsync();
            await racer.Database.CloseConnectionAsync();
        }
    }

    private async Task WaitUntilBlockedOnALockAsync(int pid, Task racing)
    {
        await using var probe = Fixture.CreateDbContext(Fixture.TenantId);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (racing.IsCompleted)
                throw new InvalidOperationException("The second write finished while the first was uncommitted", racing.Exception);

            var blocked = await probe.Database
                .SqlQuery<bool>($"""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock') AS "Value"
                    """)
                .SingleAsync();
            if (blocked)
                return;

            await Task.Delay(20);
        }

        throw new TimeoutException("The second write never blocked on the first");
    }

    private async Task<List<(Guid Id, string? OriginalId, short? SleepScore)>> StoredAsync()
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        return (await db.SleepSessions.AsNoTracking().ToListAsync())
            .Select(s => (s.Id, s.OriginalId, s.SleepScore))
            .ToList();
    }

    private static SleepSessionRepository Repository(ITenantDbContextFactory contexts) => new(contexts);

    private static SleepSession Session(Guid? id, string? originalId, short score) => new()
    {
        Id = id?.ToString(),
        StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
        EndTime = new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
        Type = SleepSessionType.Overnight,
        DetectionMethod = SleepDetectionMethod.Manual,
        Source = SleepSource.Manual,
        DurationMs = 28_800_000,
        TotalSleepMs = 28_800_000,
        OriginalId = originalId,
        SleepScore = score,
    };

    private sealed class Pinned(NocturneDbContext context) : ITenantDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(context);
    }
}
