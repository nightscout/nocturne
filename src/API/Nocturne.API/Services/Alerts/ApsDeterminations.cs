using Nocturne.API.Services.V4;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The loop's determination an APS snapshot records, as <c>eventual_glucose</c> and
/// <c>carbs_required</c> read it (docs/alerts/engine-semantics.md §4).
/// </summary>
internal static class ApsDeterminations
{
    /// <remarks>
    /// The carbohydrate is the <c>carbsReq</c> of the command the eventual glucose came from
    /// (<see cref="ApsSnapshotCurves.StoredFreshestCommand"/>). oref leaves the key out when the
    /// carbohydrate it needs is under its own threshold, or the low it foresees is too far ahead to
    /// act on, so a completed oref determination without it is 0. A command with no
    /// <c>eventualBG</c> is not one: oref's early returns (a noisy or calibrating sensor, old
    /// readings, no profile) carry a reason and none of a determination's values. Loop reports no
    /// such value, and a command that is missing or does not parse says nothing. Each of those is
    /// null, which the engine reads as absent. A stored <c>"carbsReq": null</c> cannot be told from
    /// an absent key: the decomposer writes commands without their null members.
    /// </remarks>
    public static ApsDetermination Of(ApsSnapshot snapshot) => new(
        DateTime.SpecifyKind(snapshot.Timestamp, DateTimeKind.Utc),
        snapshot.EventualBg?.ToDecimal(),
        CarbsRequired(snapshot));

    private static decimal? CarbsRequired(ApsSnapshot snapshot) =>
        LoopHealthCalculator.IsOref(snapshot.AidAlgorithm)
        && ApsSnapshotCurves.StoredFreshestCommand(snapshot) is { EventualBG: not null } command
            ? command.CarbsReq ?? 0
            : null;
}
