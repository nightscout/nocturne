using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Uploads;

/// <summary>
/// The current tenant's rejected uploads: records answered to their uploader but not stored.
/// </summary>
public interface IRejectedUploadStore
{
    /// <summary>
    /// Keeps <paramref name="upload"/>, or counts another occurrence of the one already kept under
    /// its record type and identity.
    /// </summary>
    /// <returns>Whether it was not already kept.</returns>
    Task<bool> UpsertAsync(RejectedUpload upload, CancellationToken ct = default);

    /// <summary>Most recently seen first.</summary>
    Task<PaginatedResponse<RejectedUpload>> GetAsync(int limit, int offset, CancellationToken ct = default);

    /// <returns>How many were removed.</returns>
    Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}

/// <summary>
/// Keeps refused records in the tenant's rejected uploads and tells the owner about new ones.
/// </summary>
public interface IRejectedUploadRecorder
{
    /// <summary>
    /// Records <paramref name="refused"/>, each on its own so one that cannot be kept does not lose
    /// the rest. Never throws for a failure to record: the write it reports on has been answered.
    /// </summary>
    Task RecordTreatmentsAsync(
        IReadOnlyList<BulkWriteRejection<Treatment>> refused, CancellationToken ct = default);

    /// <summary>
    /// Removes the rejected uploads named by <paramref name="ids"/>, and the owner's notification
    /// when none are left.
    /// </summary>
    /// <returns>How many were removed.</returns>
    Task<int> ClearAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}
