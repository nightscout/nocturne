using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Profiles;

/// <summary>
/// Finds the current tenant's live schedules that still hold mmol/L values the readers take as mg/dL,
/// by the rules of the <c>RepairRawMmolProfiles</c> migration, and records an owner's answer that a
/// listed ISF is already mg/dL.
/// </summary>
public interface IRawMmolProfileDetector
{
    /// <summary>The listed schedules, targets first, each kind by profile name and timestamp.</summary>
    Task<IReadOnlyList<RawMmolSchedule>> DetectAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks the store of a sensitivity schedule as holding a mg/dL ISF, so its ISF is not listed again
    /// until a write replaces the store's therapy settings row.
    /// </summary>
    /// <returns>False when the schedule is not found or its store has no therapy settings row.</returns>
    Task<bool> DismissIsfAsync(Guid sensitivityScheduleId, CancellationToken ct = default);
}
