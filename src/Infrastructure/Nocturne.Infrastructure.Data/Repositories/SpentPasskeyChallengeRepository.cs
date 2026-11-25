using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="ISpentPasskeyChallengeRepository"/> over <c>spent_passkey_challenges</c>, one
/// platform context per call. Relational providers only: the spend is one
/// <c>INSERT ... ON CONFLICT DO NOTHING</c>, so the database's primary key decides which of two
/// concurrent spends wins.
/// </summary>
public class SpentPasskeyChallengeRepository : ISpentPasskeyChallengeRepository
{
    private readonly INocturneDbContextFactory _contexts;

    public SpentPasskeyChallengeRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A retried insert whose first attempt committed finds its own row and answers false, so a
    /// lost acknowledgement refuses the ceremony rather than admitting it twice.
    /// </remarks>
    public virtual async Task<bool> TrySpendAsync(string challenge, DateTime expiresAt, CancellationToken ct)
    {
        await using var db = await _contexts.CreatePlatformAsync(ct);
        var inserted = await db.Database.ExecuteSqlAsync(
            $"INSERT INTO spent_passkey_challenges (challenge, expires_at) VALUES ({challenge}, {DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)}) ON CONFLICT (challenge) DO NOTHING",
            ct);
        return inserted == 1;
    }

    /// <inheritdoc/>
    public virtual async Task<int> DeleteExpiredAsync(DateTime cutoff, int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        await using var db = await _contexts.CreatePlatformAsync(ct);
        return await db.SpentPasskeyChallenges
            .Where(c => c.ExpiresAt < cutoff)
            .OrderBy(c => c.ExpiresAt)
            .Take(batchSize)
            .ExecuteDeleteAsync(ct);
    }
}
