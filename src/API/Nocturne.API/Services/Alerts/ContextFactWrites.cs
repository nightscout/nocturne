using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The host's record-to-fact map (docs/alerts/engine-semantics.md §5.2): which context facts a
/// committed write of each V4 record type moves, and for each fact whether every write that moves it
/// announces itself (<see cref="IRecordChangePublisher"/>) and whether it also moves with the clock.
/// </summary>
/// <remarks>
/// A write evaluates the enabled rules that read a fact it moved (<see cref="AlertFactEvaluationScheduler"/>),
/// and the sweep may wait for a rule's next change only when every fact the rule reads
/// <see cref="SweepMayWaitFor"/>. An entry that names a fact the write does not move costs an
/// evaluation; one that misses a fact costs an alert, so an entry errs wide.
/// </remarks>
internal static class ContextFactWrites
{
    public const string Glucose = "glucose";
    public const string Predictions = "predictions";
    public const string Insulin = "insulin";
    public const string Carbs = "carbs";
    public const string Pump = "pump";
    public const string Aps = "aps";
    public const string Uploader = "uploader";
    public const string Transmitter = "transmitter";
    public const string StateSpans = "state_spans";
    public const string DeviceAges = "device_ages";
    public const string Trackers = "trackers";
    public const string Sleep = "sleep";
    public const string ActiveAlerts = "active_alerts";
    public const string DoNotDisturb = "do_not_disturb";
    public const string TimeZone = "time_zone";
    public const string SensorState = "sensor_state";

    /// <param name="EveryWriteAnnounces">
    /// Every write that moves the fact reaches the record-changed event. False where a store writes it
    /// without announcing (state spans, trackers, sleep sessions, alert instances, Do Not Disturb), or
    /// where deduplication re-picks which copy reads are shown with no announcement: device events are
    /// deduplicated, so a merge can move a site change or sensor start by its match tolerance.
    /// Glucose is deduplicated too, and stays true because the sweep keys a recorded next change by the
    /// glucose facts it reads itself, so a re-pick that changes the selected reading is seen there.
    /// </param>
    /// <param name="MovesWithClock">
    /// The fact changes with no write: insulin and carbs on board decay, a temp basal or a scheduled
    /// basal depletes the reservoir estimate, a forecast ages, and the tenant's calendar and the
    /// target-range schedule entry follow the local clock (a <c>glucose_bucket</c> reads
    /// <see cref="TimeZone"/> for that reason).
    /// </param>
    /// <remarks>
    /// <see cref="SensorState"/> is moved by a reading (one with no value is its own state) and by a
    /// device status that reports the sensor, whose extras write announces itself. A reported state
    /// stops counting at an instant the context carries (<see cref="SensorStateSnapshot.Until"/>), which
    /// the engine reports as a next change, so the fact does not move with the clock.
    /// </remarks>
    internal readonly record struct FactTrigger(bool EveryWriteAnnounces, bool MovesWithClock);

    public static readonly IReadOnlyDictionary<string, FactTrigger> Facts = new Dictionary<string, FactTrigger>(StringComparer.Ordinal)
    {
        [Glucose] = new(EveryWriteAnnounces: true, MovesWithClock: false),
        [Predictions] = new(EveryWriteAnnounces: true, MovesWithClock: true),
        [Insulin] = new(EveryWriteAnnounces: false, MovesWithClock: true),
        [Carbs] = new(EveryWriteAnnounces: true, MovesWithClock: true),
        [Pump] = new(EveryWriteAnnounces: true, MovesWithClock: true),
        [Aps] = new(EveryWriteAnnounces: true, MovesWithClock: false),
        [Uploader] = new(EveryWriteAnnounces: true, MovesWithClock: false),
        [Transmitter] = new(EveryWriteAnnounces: true, MovesWithClock: false),
        [StateSpans] = new(EveryWriteAnnounces: false, MovesWithClock: false),
        [DeviceAges] = new(EveryWriteAnnounces: false, MovesWithClock: false),
        [Trackers] = new(EveryWriteAnnounces: false, MovesWithClock: false),
        [Sleep] = new(EveryWriteAnnounces: false, MovesWithClock: false),
        [ActiveAlerts] = new(EveryWriteAnnounces: false, MovesWithClock: false),
        [DoNotDisturb] = new(EveryWriteAnnounces: false, MovesWithClock: true),
        [TimeZone] = new(EveryWriteAnnounces: true, MovesWithClock: true),
        [SensorState] = new(EveryWriteAnnounces: true, MovesWithClock: false),
    };

    /// <summary>
    /// The facts a write of each V4 record type moves, derived ones included: an insulin delivery
    /// moves insulin on board, the reservoir estimate and the forecast. A type that moves none is
    /// listed empty, so every record type is a decision.
    /// </summary>
    public static readonly IReadOnlyDictionary<Type, string[]> MovedBy = new Dictionary<Type, string[]>
    {
        [typeof(SensorGlucose)] = [Glucose, SensorState],
        [typeof(DeviceStatusExtras)] = [SensorState],
        [typeof(MeterGlucose)] = [],
        [typeof(Calibration)] = [],
        [typeof(BGCheck)] = [],
        [typeof(Note)] = [],
        [typeof(BolusCalculation)] = [],
        [typeof(Bolus)] = [Insulin, Pump, Predictions],
        [typeof(TempBasal)] = [Insulin, Pump, Predictions],
        [typeof(BasalInjection)] = [Insulin, Predictions],
        [typeof(CarbIntake)] = [Carbs, Predictions],
        [typeof(PumpSnapshot)] = [Pump, Insulin],
        [typeof(ApsSnapshot)] = [Aps, Insulin, Carbs, Predictions],
        [typeof(UploaderSnapshot)] = [Uploader, Transmitter],
        [typeof(DeviceEvent)] = [DeviceAges, Pump],
        [typeof(ProfileActivation)] = [StateSpans, TimeZone, Glucose, Insulin, Carbs, Pump, Predictions],
        [typeof(TherapySettings)] = [TimeZone, Insulin, Carbs, Predictions],
        [typeof(BasalSchedule)] = [Insulin, Pump, Predictions],
        [typeof(CarbRatioSchedule)] = [Carbs, Predictions],
        [typeof(SensitivitySchedule)] = [Insulin, Carbs, Predictions],
        [typeof(TargetRangeSchedule)] = [Glucose, Predictions],
    };

    /// <summary>
    /// Whether the sweep may wait for the next change of a rule that reads <paramref name="fact"/>:
    /// every write that moves it evaluates the rule, and nothing else does (§5.3). Only
    /// <see cref="Glucose"/>, <see cref="Aps"/>, <see cref="Uploader"/>, <see cref="Transmitter"/> and
    /// <see cref="SensorState"/> qualify. An unknown fact does not.
    /// </summary>
    public static bool SweepMayWaitFor(string fact) =>
        Facts.TryGetValue(fact, out var trigger) && trigger.EveryWriteAnnounces && !trigger.MovesWithClock;

    /// <summary>
    /// The facts <paramref name="batch"/> moved, by wire name, and whether a type other than a reading
    /// (<see cref="SensorGlucose"/>) moved the glucose fact.
    /// </summary>
    public static MovedFacts MovedIn(RecordsChanged batch)
    {
        var moved = new HashSet<string>(StringComparer.Ordinal);
        var glucoseWithoutReading = false;
        foreach (var change in batch.Changes)
        {
            if (!MovedBy.TryGetValue(change.RecordType, out var facts))
                continue;
            moved.UnionWith(facts);
            glucoseWithoutReading |= change.RecordType != typeof(SensorGlucose) && facts.Contains(Glucose);
        }
        return new MovedFacts(moved, glucoseWithoutReading);
    }
}
