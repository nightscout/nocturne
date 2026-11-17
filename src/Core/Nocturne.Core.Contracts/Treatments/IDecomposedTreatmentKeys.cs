using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// The legacy ids the records a <see cref="Treatment"/> decomposes into are stored under, across
/// every decomposed table and the state spans of overrides and temporary targets: which ids are held
/// against a re-upload and by whom, the moving of a source's records from a client id to a server id,
/// and the upstream fingerprints a source's rows carry.
/// </summary>
public interface IDecomposedTreatmentKeys
{
    /// <summary>
    /// The ids among <paramref name="legacyIds"/> a re-upload would find stored, from any source, and of
    /// those the ones held only by a record the user deleted.
    /// </summary>
    Task<HeldLegacyIds> GetHeldLegacyIdsAsync(IReadOnlyCollection<string> legacyIds, CancellationToken ct = default);

    /// <summary>
    /// Moves <paramref name="source"/>'s records under each client id onto one of its
    /// <paramref name="equivalents"/>: the one at a stored record's time, else the first. A client id
    /// whose records the user deleted keeps them, and a copy of each is stored under every equivalent.
    /// An id something already holds is neither moved to nor copied to.
    /// </summary>
    /// <param name="source">The source whose records are moved.</param>
    /// <param name="equivalents">The treatments sharing each client id, in the order they were delivered.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<ClientIdRekey> RekeyClientIdsAsync(
        string source, IReadOnlyList<ClientIdEquivalent> equivalents, CancellationToken ct = default);

    /// <summary>
    /// The upstream fingerprint of each of <paramref name="source"/>'s rows under
    /// <paramref name="legacyIds"/>, by legacy id. An id with no such row is absent.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string?>>> GetFingerprintsFromSourceAsync(
        string source, IReadOnlyCollection<string> legacyIds, CancellationToken ct = default);

    /// <summary>
    /// Stamps <paramref name="source"/>'s rows under each legacy id of <paramref name="fingerprints"/>
    /// that carry no fingerprint yet, in one transaction.
    /// </summary>
    Task StampUnfingerprintedAsync(
        string source, IReadOnlyDictionary<string, string> fingerprints, CancellationToken ct = default);
}

/// <param name="Held">Every id a re-upload would find stored.</param>
/// <param name="DeletedByUser">The ids in <paramref name="Held"/> held only by a record the user deleted.</param>
public sealed record HeldLegacyIds(IReadOnlySet<string> Held, IReadOnlySet<string> DeletedByUser);

/// <summary>A treatment delivered under <paramref name="ClientId"/> with its own <paramref name="Id"/>.</summary>
public sealed record ClientIdEquivalent(string ClientId, string Id, long Mills);

/// <param name="Moved">The records moved onto a server id.</param>
/// <param name="Written">Whether anything was moved or copied.</param>
public sealed record ClientIdRekey(int Moved, bool Written);
