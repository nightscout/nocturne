using Nocturne.Core.Models.SetupHub;

namespace Nocturne.Core.Contracts.SetupHub;

/// <summary>
/// One item on the setup hub. An implementation registered in DI is all the server needs to list
/// it; <see cref="SetupHubItemKey"/> gives it its place in the order.
/// </summary>
public interface ISetupHubItem
{
    SetupHubItemKey Key { get; }

    /// <summary>
    /// Whether to list the item for a tenant it has never been listed for. Once listed it stays
    /// listed, so this is asked at most until the first time it answers yes.
    /// </summary>
    Task<bool> IsOfferedAsync(CancellationToken ct) => Task.FromResult(true);

    /// <summary>
    /// Whether what the item sets up already works, judged from the tenant's own data. A yes marks
    /// the item <see cref="SetupHubItemState.Done"/> for good.
    /// </summary>
    Task<bool> WorksAsync(CancellationToken ct);
}

/// <summary>The current tenant's setup hub. Every read settles each item against current data.</summary>
public interface ISetupHubService
{
    Task<SetupHubStatus> GetAsync(CancellationToken ct);

    /// <summary>
    /// Marks a listed item <see cref="SetupHubItemState.NotForMe"/> or reopens it.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The item is not listed for this tenant.</exception>
    /// <exception cref="InvalidOperationException">The item is done, which never reverts.</exception>
    Task<SetupHubStatus> SetStateAsync(SetupHubItemKey key, SetupHubItemState state, CancellationToken ct);

    /// <summary>Hides the dashboard strip until the hub's revision moves on from <paramref name="revision"/>.</summary>
    Task<SetupHubStatus> DismissStripAsync(string revision, CancellationToken ct);
}
