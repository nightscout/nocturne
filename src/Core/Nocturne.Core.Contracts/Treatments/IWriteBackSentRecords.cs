using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// The upstream documents of each stored treatment row's record a pull may bring back as an echo, by
/// their <see cref="WriteBackFingerprint"/>, in the list <see cref="WriteBackFingerprint.Stored"/>
/// describes. A row holds them only once upstream has accepted a send of its record, so their presence
/// also answers whether write-back ever sent it.
/// </summary>
public interface IWriteBackSentRecords
{
    /// <summary>
    /// Stamps the live rows of each of <paramref name="records"/> with the documents upstream accepted
    /// it as (<see cref="WriteBackFingerprint.Accepted"/>).
    /// </summary>
    Task RecordSentAsync(IReadOnlyList<WriteBackSentRecord> records, CancellationToken ct = default);

    /// <summary>
    /// Adds the documents each of <paramref name="records"/> is about to go upstream as to its live rows
    /// that already hold some (<see cref="WriteBackFingerprint.Sending"/>), before the send, so a pull
    /// during it, or after it fails part way, reads them as echoes. A row that holds none is left so.
    /// </summary>
    Task RecordSendingAsync(IReadOnlyList<WriteBackSentRecord> records, CancellationToken ct = default);

    /// <summary>The ids among <paramref name="legacyIds"/> whose record write-back has sent upstream.</summary>
    /// <remarks>
    /// It reads past the member history clamp, so its answer may only feed a decision made inside
    /// Nocturne, such as whether to remove a record upstream. It must never be returned to a caller: a
    /// clamped member would learn of a record dated before the window.
    /// </remarks>
    Task<IReadOnlySet<string>> GetSentUpstreamAsync(IReadOnlyCollection<string> legacyIds, CancellationToken ct = default);
}

/// <summary>A stored treatment and the documents it goes upstream as.</summary>
/// <param name="LegacyId">The record's legacy id, which names its rows.</param>
/// <param name="RecordId">The record's own uuid, which names its rows when it has no legacy id.</param>
/// <param name="Documents">
/// The <see cref="WriteBackFingerprint"/> of each document: one, or two for a meal
/// <see cref="PreBolusSplit.ForWriteBack"/> splits into its bolus and its carbs.
/// </param>
public sealed record WriteBackSentRecord(string? LegacyId, Guid? RecordId, IReadOnlyList<string> Documents);
