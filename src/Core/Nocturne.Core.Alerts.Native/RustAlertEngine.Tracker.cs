namespace Nocturne.Core.Alerts.Native;

public static partial class RustAlertEngine
{
    /// <summary>
    /// Advances the excursion state machine for one evaluation's truth, decided elsewhere
    /// (docs/alerts/engine-semantics.md §6).
    /// </summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request or returned a response <see cref="ParseTrackerResponse"/> refuses.
    /// </exception>
    public static RustTrackerResponse TrackerProcess(RustTrackerProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ParseTrackerResponse(AlertsInterop.TrackerProcess(Serialize(request)), "tracker_process");
    }

    /// <summary>Closes the tracker's excursion, if it has one, from any state (§6.2).</summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request or returned a response <see cref="ParseTrackerResponse"/> refuses.
    /// </exception>
    public static RustTrackerResponse TrackerForceClose(RustTrackerForceCloseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ParseTrackerResponse(AlertsInterop.TrackerForceClose(Serialize(request)), "tracker_force_close");
    }

    /// <summary>
    /// Closes the tracker's excursion when it is in hysteresis and the window has elapsed (§6.1).
    /// The returned tracker can differ even on a <c>none</c> transition, so persist it either way.
    /// </summary>
    /// <exception cref="RustAlertEngineException">
    /// The engine rejected the request or returned a response <see cref="ParseTrackerResponse"/> refuses.
    /// </exception>
    public static RustTrackerResponse TrackerCloseElapsedHysteresis(RustTrackerCloseElapsedHysteresisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ParseTrackerResponse(
            AlertsInterop.TrackerCloseElapsedHysteresis(Serialize(request)), "tracker_close_elapsed_hysteresis");
    }

    /// <summary>
    /// A successful tracker envelope carries <c>transition</c> and <c>tracker</c>. The transition
    /// carries <c>close_reason</c> exactly when it is a close, and a tracker holding a <c>state</c>
    /// carries its <c>updated_at</c>.
    /// </summary>
    internal static RustTrackerResponse ParseTrackerResponse(string responseJson, string operation)
    {
        var response = ParseResponse<RustTrackerResponse>(responseJson, operation);
        Require(response.Transition is not null, operation, "transition");
        Require(
            (response.Transition!.Type == RustTransition.Closed) == (response.Transition.CloseReason is not null),
            operation, "transition.close_reason");
        Require(response.Tracker is not null, operation, "tracker");
        Require(response.Tracker!.State is null || response.Tracker.UpdatedAt is not null, operation, "tracker.updated_at");
        return response;
    }
}
