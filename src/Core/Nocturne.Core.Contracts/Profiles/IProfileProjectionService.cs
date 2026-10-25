using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;

namespace Nocturne.Core.Contracts.Profiles;

/// <summary>
/// Reconstructs legacy <see cref="Profile"/> records from V4 schedule data for V1/V3 API backward compatibility.
/// </summary>
/// <remarks>
/// This is the read-side projection that queries <see cref="Models.V4.TherapySettings"/> and its sibling
/// schedule repositories (<see cref="Models.V4.BasalSchedule"/>, <see cref="Models.V4.CarbRatioSchedule"/>,
/// <see cref="Models.V4.SensitivitySchedule"/>, <see cref="Models.V4.TargetRangeSchedule"/>) and assembles
/// them into the monolithic <see cref="Profile"/> shape expected by legacy Nightscout clients.
/// </remarks>
public interface IProfileProjectionService
{
    /// <summary>
    /// The newest stored profile document, as <see cref="GetProfilesAsync"/> ranks them.
    /// </summary>
    /// <returns>The newest profile document, or <c>null</c> when none is stored.</returns>
    Task<Profile?> GetCurrentProfileAsync(CancellationToken ct = default);

    /// <summary>
    /// The whole profile document <paramref name="id"/> resolves to through <see cref="ResolveDocumentIdAsync"/>.
    /// </summary>
    /// <param name="id">Legacy MongoDB ObjectId or GUID identifier.</param>
    /// <returns>The matching profile, or <c>null</c> if not found.</returns>
    Task<Profile?> GetProfileByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The id of the stored profile document <paramref name="id"/> names: a row's legacy or own id,
    /// a document id, or the <see cref="MongoObjectId.Coerce"/> form v3 serves it under. Reads and
    /// writes by id resolve through this, so each reaches the document the other does.
    /// </summary>
    /// <returns>The document id, or <c>null</c> when nothing stored answers to <paramref name="id"/>.</returns>
    Task<string?> ResolveDocumentIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// A page of the stored profile documents, each carrying every one of its stores: newest first
    /// when <paramref name="descending"/> is <c>true</c>, otherwise oldest first, the page cut from
    /// that order.
    /// </summary>
    /// <param name="count">Maximum number of documents to return.</param>
    /// <param name="skip">Number of documents to skip.</param>
    /// <param name="descending">Whether the newest document comes first.</param>
    Task<IEnumerable<Profile>> GetProfilesAsync(int count = 10, int skip = 0, bool descending = true, CancellationToken ct = default);

    /// <summary>
    /// Profiles whose <c>srvModified</c> — the newest write stamp among the therapy settings and the
    /// schedules they are assembled from — falls after <paramref name="cursorMills"/>, oldest first,
    /// for the v3 <c>history/{lastModified}</c> endpoint.
    /// </summary>
    /// <param name="cursorMills">The client's cursor, in Unix milliseconds.</param>
    /// <param name="limit">The page size; a page may exceed it to finish its last millisecond.</param>
    Task<ModifiedSincePage<Profile>> GetProfilesModifiedSinceAsync(
        long cursorMills, int limit, CancellationToken ct = default);

    /// <summary>
    /// The number of stored profile documents <see cref="GetProfilesAsync"/> pages through.
    /// </summary>
    /// <param name="find">Optional filter string (reserved for future use).</param>
    Task<long> CountProfilesAsync(string? find = null, CancellationToken ct = default);
}
