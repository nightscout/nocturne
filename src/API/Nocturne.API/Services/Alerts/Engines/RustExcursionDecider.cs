using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// <see cref="IExcursionDecider"/> over the native tracker entry points. Pure: state goes in
/// as data, and the caller persists the decision.
/// </summary>
/// <param name="errors">Where its failures are counted.</param>
/// <param name="engine">The <c>engine</c> tag they are counted under.</param>
internal sealed class RustExcursionDecider(AlertEngineErrors errors, string engine) : IExcursionDecider
{
    public TrackerDecision Process(
        Guid ruleId, AlertTrackerState? state, TrackerConfig config, bool conditionMet, DateTime now) =>
        errors.Track("tracker_process", engine, () => ToDecision(RustAlertEngine.TrackerProcess(new RustTrackerProcessRequest
        {
            Tracker = RustEnvelopeMapper.BuildTracker(state),
            Now = now,
            Config = ToWire(config),
            ConditionMet = conditionMet,
        })));

    public TrackerDecision ForceClose(
        Guid ruleId, AlertTrackerState? state, ExcursionCloseReason reason, DateTime now) =>
        errors.Track("tracker_force_close", engine, () => ToDecision(RustAlertEngine.TrackerForceClose(new RustTrackerForceCloseRequest
        {
            Tracker = RustEnvelopeMapper.BuildTracker(state),
            Now = now,
            Reason = RustEnvelopeMapper.CloseReasonToRust(reason),
        })));

    public TrackerDecision CloseElapsedHysteresis(
        Guid ruleId, AlertTrackerState? state, TrackerConfig config, DateTime now) =>
        errors.Track("tracker_close_elapsed_hysteresis", engine, () => ToDecision(
            RustAlertEngine.TrackerCloseElapsedHysteresis(new RustTrackerCloseElapsedHysteresisRequest
            {
                Tracker = RustEnvelopeMapper.BuildTracker(state),
                Now = now,
                Config = ToWire(config),
            })));

    private static RustTrackerConfig ToWire(TrackerConfig config) => new()
    {
        ConfirmationReadings = config.ConfirmationReadings,
        HysteresisMinutes = config.HysteresisMinutes,
    };

    private static TrackerDecision ToDecision(RustTrackerResponse response) => new(
        RustEnvelopeMapper.TransitionFromWire(response.Transition!.Type),
        response.Transition.CloseReason is { } reason ? RustEnvelopeMapper.CloseReasonFromWire(reason) : null,
        RustEnvelopeMapper.PostStateFromWire(response.Tracker!));
}
