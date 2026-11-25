using Nocturne.API.Services.V4;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.DeviceStatuses;

/// <summary>The decomposition of a batch of device statuses, with what each status wrote.</summary>
internal sealed class DeviceStatusDecomposition : GroupedDecomposition
{
    private readonly HashSet<DeviceStatus> _invalidated = new(ReferenceEqualityComparer.Instance);

    /// <summary>The statuses sent with <c>isValid: false</c> that deleted the live status their id names.</summary>
    internal IReadOnlySet<DeviceStatus> Invalidated => _invalidated;

    internal void AddInvalidated(DeviceStatus status) => _invalidated.Add(status);
}
