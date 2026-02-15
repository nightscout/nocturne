using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Decomposes a legacy DeviceStatus record into typed v4 snapshot tables.
/// </summary>
public interface IDeviceStatusDecomposer
{
    /// <summary>
    /// Extracts APS, pump, and uploader snapshots from a DeviceStatus record
    /// and persists them to v4 tables. Idempotent via LegacyId matching.
    /// </summary>
    Task<DecompositionResult> DecomposeAsync(DeviceStatus deviceStatus, CancellationToken ct = default);
}
