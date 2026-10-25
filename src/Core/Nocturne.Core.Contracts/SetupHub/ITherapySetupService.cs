using Nocturne.Core.Models.SetupHub;

namespace Nocturne.Core.Contracts.SetupHub;

/// <summary>The setup hub's therapy settings item: review what arrived, or enter it by hand.</summary>
public interface ITherapySetupService
{
    Task<TherapyReview> GetReviewAsync(CancellationToken ct);

    /// <summary>
    /// Records that the synced or imported profile under review matches the owner's app. The
    /// confirmation holds for that profile only; a newer one is reviewed afresh.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no synced or imported profile to confirm.</exception>
    Task<TherapyReview> ConfirmAsync(CancellationToken ct);

    /// <summary>
    /// Writes a hand-entered profile as the tenant's default. Sensitivity and target values are in
    /// <paramref name="glucoseUnits"/> and stored in mg/dL. A schedule left wholly blank is not written.
    /// </summary>
    /// <exception cref="InvalidOperationException">A therapy profile already exists.</exception>
    /// <exception cref="ArgumentException">The entry is incomplete or malformed; the message says how.</exception>
    Task<TherapyReview> EnterAsync(
        string glucoseUnits,
        IReadOnlyList<TherapyEntryInput> basal,
        IReadOnlyList<TherapyEntryInput> carbRatio,
        IReadOnlyList<TherapyEntryInput> sensitivity,
        IReadOnlyList<TargetEntryInput> targetRange,
        CancellationToken ct);
}
