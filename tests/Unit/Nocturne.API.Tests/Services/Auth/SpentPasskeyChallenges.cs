using System.Collections.Concurrent;
using Nocturne.Core.Contracts.Auth;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// The spent-challenge record for tests on the InMemory provider, whose context runs no SQL. One
/// winner per challenge, as the table's primary key gives.
/// </summary>
internal sealed class SpentPasskeyChallenges : ISpentPasskeyChallengeRepository
{
    public ConcurrentDictionary<string, DateTime> Spent { get; } = new();

    public Task<bool> TrySpendAsync(string challenge, DateTime expiresAt, CancellationToken ct) =>
        Task.FromResult(Spent.TryAdd(challenge, expiresAt));

    public Task<int> DeleteExpiredAsync(DateTime cutoff, int batchSize, CancellationToken ct) =>
        throw new NotSupportedException();
}
