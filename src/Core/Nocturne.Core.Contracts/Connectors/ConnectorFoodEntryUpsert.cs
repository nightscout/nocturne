using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>What <see cref="IConnectorFoodEntryRepository.UpsertAsync"/> committed.</summary>
/// <param name="Entries">
/// One entry per distinct connector source and trimmed entry id among the imports that named both,
/// in the order the keys first appear. Imports sharing a key resolve to one entry, so a caller
/// that acts on each entry acts once.
/// </param>
/// <param name="EntriesToMatch">
/// The entries meal matching has something new to see: created, restored from a withdrawal, or
/// moved to a different consumed time while still pending.
/// </param>
public sealed record ConnectorFoodEntryUpsert(
    IReadOnlyList<ConnectorFoodEntry> Entries,
    IReadOnlySet<Guid> EntriesToMatch);
