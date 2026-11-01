using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.DeviceStatuses;

/// <summary>The decomposition of a batch of device statuses, with what each status wrote.</summary>
internal sealed class DeviceStatusDecomposition : DecompositionResult
{
    /// <summary>
    /// Per status, in batch order: whether a record of its group was created or updated. A status
    /// whose every record was already stored as it decomposes wrote nothing.
    /// </summary>
    public IReadOnlyList<bool> Wrote { get; set; } = [];
}
