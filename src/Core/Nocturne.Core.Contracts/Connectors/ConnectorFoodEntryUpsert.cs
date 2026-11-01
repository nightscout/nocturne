using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>What <see cref="IConnectorFoodEntryRepository.UpsertAsync"/> committed.</summary>
/// <param name="Entries">One entry per import that named a source and an entry id, in import order.</param>
/// <param name="EntriesToMatch">
/// The entries meal matching has something new to see: created, restored from a withdrawal, or
/// moved to a different consumed time while still pending.
/// </param>
public sealed record ConnectorFoodEntryUpsert(
    IReadOnlyList<ConnectorFoodEntry> Entries,
    IReadOnlySet<Guid> EntriesToMatch);
