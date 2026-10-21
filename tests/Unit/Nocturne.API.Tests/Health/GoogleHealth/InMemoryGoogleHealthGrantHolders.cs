using System.Collections.Concurrent;
using Nocturne.Core.Contracts.Health;

namespace Nocturne.API.Tests.Health.GoogleHealth;

/// <summary>An instance's grant holders, shared by the services of every tenant a test builds.</summary>
internal sealed class InMemoryGoogleHealthGrantHolders : IGoogleHealthGrantHolders
{
    private readonly ConcurrentDictionary<Guid, string> holders = new();

    public IReadOnlyDictionary<Guid, string> Holders => holders;

    public Task HoldAsync(Guid tenantId, string accountKey, CancellationToken ct)
    {
        holders[tenantId] = accountKey;
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(Guid tenantId, CancellationToken ct)
    {
        holders.TryRemove(tenantId, out _);
        return Task.CompletedTask;
    }

    /// <summary>False to answer as if the holders could not be read.</summary>
    public bool Readable { get; set; } = true;

    public Task<GoogleHealthGrantSharing> SharingAsync(Guid tenantId, string accountKey, CancellationToken ct) =>
        Task.FromResult(!Readable
            ? GoogleHealthGrantSharing.Undetermined
            : holders.Any(holder => holder.Key != tenantId && holder.Value == accountKey)
                ? GoogleHealthGrantSharing.Shared
                : GoogleHealthGrantSharing.NotShared);
}
