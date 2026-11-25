using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="SpentPasskeyChallengeRepository"/> on SQLite: a challenge is spent once, and the sweep
/// deletes only challenges past the cutoff, a bounded batch at a time.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SpentPasskeyChallengeRepositoryTests : IDisposable
{
    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly NocturneDbContext _context;

    public SpentPasskeyChallengeRepositoryTests() => _context = _db.CreateContext(Guid.Empty);

    private SpentPasskeyChallengeRepository Challenges() => new(_context.AsFactory());

    [Fact]
    public async Task A_challenge_is_spent_by_the_first_call_only()
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(5);

        (await Challenges().TrySpendAsync("challenge-a", expiresAt, default)).Should().BeTrue();
        (await Challenges().TrySpendAsync("challenge-a", expiresAt, default)).Should().BeFalse();
        (await Challenges().TrySpendAsync("challenge-b", expiresAt, default)).Should().BeTrue();

        var rows = await _context.SpentPasskeyChallenges.AsNoTracking().OrderBy(c => c.Challenge).ToListAsync();
        rows.Select(c => c.Challenge).Should().Equal("challenge-a", "challenge-b");
        rows[0].ExpiresAt.Should().BeCloseTo(expiresAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task The_sweep_deletes_only_challenges_expired_before_the_cutoff_a_batch_at_a_time()
    {
        var cutoff = DateTime.UtcNow;
        foreach (var i in Enumerable.Range(0, 3))
            await Challenges().TrySpendAsync($"old-{i}", cutoff.AddMinutes(-10 - i), default);
        await Challenges().TrySpendAsync("at-cutoff", cutoff, default);
        await Challenges().TrySpendAsync("live", cutoff.AddMinutes(5), default);

        (await Challenges().DeleteExpiredAsync(cutoff, batchSize: 2, default)).Should().Be(2);
        (await Challenges().DeleteExpiredAsync(cutoff, batchSize: 2, default)).Should().Be(1);
        (await Challenges().DeleteExpiredAsync(cutoff, batchSize: 2, default)).Should().Be(0);

        (await _context.SpentPasskeyChallenges.AsNoTracking().Select(c => c.Challenge).OrderBy(c => c).ToListAsync())
            .Should().Equal("at-cutoff", "live");
    }

    [Fact]
    public async Task A_sweep_of_no_rows_at_a_time_is_refused()
    {
        var act = () => Challenges().DeleteExpiredAsync(DateTime.UtcNow, batchSize: 0, default);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }
}
