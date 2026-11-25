using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="SpentPasskeyChallengeRepository"/> on PostgreSQL as the runtime role: of any number of
/// spends of one challenge, at once or not, exactly one wins, and no caller's transaction can undo
/// it.
/// </summary>
[Trait("Category", "Integration")]
public class SpentPasskeyChallengeRepositoryPostgresTests(SpentPasskeyChallengeRepositoryPostgresTests.Database database)
    : IClassFixture<SpentPasskeyChallengeRepositoryPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync() =>
            AppConnectionString = (await SharedPostgres.CreateMigratedDatabaseAsync("spent_passkey_challenges")).AppConnectionString;

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private NocturneDbContext Context() =>
        TestContexts.Context(database.AppConnectionString);

    private SuppliedContextFactory Contexts() => new(Context);

    private SpentPasskeyChallengeRepository Challenges() => new(Contexts());

    [Fact]
    public async Task Of_many_spends_of_one_challenge_at_once_exactly_one_wins()
    {
        var challenge = $"concurrent-{Guid.NewGuid():N}";
        using var start = new ManualResetEventSlim();

        var spends = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await Challenges().TrySpendAsync(challenge, DateTime.UtcNow.AddMinutes(5), default);
        })).ToList();
        start.Set();

        (await Task.WhenAll(spends)).Count(won => won).Should().Be(1);
    }

    [Fact]
    public async Task A_spend_outlives_a_transaction_the_caller_rolls_back()
    {
        var challenge = $"rolled-back-{Guid.NewGuid():N}";

        await using (var caller = await Contexts().CreatePlatformAsync())
        {
            await using var transaction = await caller.Database.BeginTransactionAsync();
            (await Challenges().TrySpendAsync(challenge, DateTime.UtcNow.AddMinutes(5), default)).Should().BeTrue();
            await transaction.RollbackAsync();
        }

        (await Challenges().TrySpendAsync(challenge, DateTime.UtcNow.AddMinutes(5), default)).Should().BeFalse();
    }

    [Fact]
    public async Task The_sweep_deletes_only_challenges_expired_before_the_cutoff_a_batch_at_a_time()
    {
        var cutoff = DateTime.UtcNow.AddYears(-1);
        var prefix = Guid.NewGuid().ToString("N");
        foreach (var i in Enumerable.Range(0, 3))
            await Challenges().TrySpendAsync($"{prefix}-old-{i}", cutoff.AddMinutes(-10 - i), default);
        await Challenges().TrySpendAsync($"{prefix}-live", cutoff.AddMinutes(5), default);

        (await Challenges().DeleteExpiredAsync(cutoff, batchSize: 2, default)).Should().Be(2);
        (await Challenges().DeleteExpiredAsync(cutoff, batchSize: 2, default)).Should().Be(1);

        await using var db = await Contexts().CreatePlatformAsync();
        (await db.SpentPasskeyChallenges.AsNoTracking()
                .Where(c => c.Challenge.StartsWith(prefix)).Select(c => c.Challenge).ToListAsync())
            .Should().Equal($"{prefix}-live");
    }
}
