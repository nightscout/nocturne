using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Uploads;

/// <summary>
/// The current tenant's rejected uploads: records answered to their uploader but not stored.
/// </summary>
public interface IRejectedUploadStore
{
    Task AddAsync(IReadOnlyList<RejectedUpload> uploads, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<PaginatedResponse<RejectedUpload>> GetAsync(int limit, int offset, CancellationToken ct = default);

    /// <returns>How many were removed.</returns>
    Task<int> ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// Keeps refused records in the tenant's rejected uploads and tells the owner they exist.
/// </summary>
public interface IRejectedUploadRecorder
{
    /// <summary>
    /// Records <paramref name="refused"/>. Never throws for a failure to record: the write it
    /// reports on has already been answered.
    /// </summary>
    Task RecordTreatmentsAsync(
        IReadOnlyList<BulkWriteRejection<Treatment>> refused, CancellationToken ct = default);

    /// <summary>Removes every rejected upload and the owner's notification about them.</summary>
    /// <returns>How many were removed.</returns>
    Task<int> ClearAsync(CancellationToken ct = default);
}
