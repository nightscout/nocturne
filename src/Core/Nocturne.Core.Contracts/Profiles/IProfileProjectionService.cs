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
    /// Get the most recent profile by querying the latest <see cref="Models.V4.TherapySettings"/> record
    /// and assembling all correlated schedule records into a <see cref="Profile"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The most recent profile, or <c>null</c> if no therapy settings exist.</returns>
    Task<Profile?> GetCurrentProfileAsync(CancellationToken ct = default);

    /// <summary>
    /// Get a profile by its legacy MongoDB ObjectId or GUID string.
    /// </summary>
    /// <param name="id">Legacy MongoDB ObjectId or GUID identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching profile, or <c>null</c> if not found.</returns>
    Task<Profile?> GetProfileByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Get a paginated list of profiles, ordered by timestamp descending.
    /// </summary>
    /// <param name="count">Maximum number of profiles to return.</param>
    /// <param name="skip">Number of profiles to skip for pagination.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Projected profiles from V4 therapy settings.</returns>
    Task<IEnumerable<Profile>> GetProfilesAsync(int count = 10, int skip = 0, CancellationToken ct = default);

    /// <summary>
    /// Profiles whose <c>srvModified</c> — the newest write stamp among the therapy settings and the
    /// schedules they are assembled from — falls after <paramref name="cursorMills"/>, oldest first,
    /// for the v3 <c>history/{lastModified}</c> endpoint.
    /// </summary>
    /// <param name="cursorMills">The client's cursor, in Unix milliseconds.</param>
    /// <param name="limit">The page size; a page may exceed it to finish its last millisecond.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ModifiedSincePage<Profile>> GetProfilesModifiedSinceAsync(
        long cursorMills, int limit, CancellationToken ct = default);

    /// <summary>
    /// Count the total number of distinct profile records.
    /// </summary>
    /// <param name="find">Optional filter string (reserved for future use).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of distinct therapy settings records.</returns>
    Task<long> CountProfilesAsync(string? find = null, CancellationToken ct = default);
}
