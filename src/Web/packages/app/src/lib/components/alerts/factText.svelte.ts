import type { FactGroup, LeafKind } from "./factCatalog";

export function factLabel(kind: LeafKind): string {
  switch (kind) {
    case "threshold":
      return "Glucose threshold";
    case "glucose_bucket":
      return "Glucose bucket";
    case "predicted":
      return "Predicted glucose";
    case "rate_of_change":
      return "Rate of change";
    case "trend":
      return "Glucose trend";
    case "staleness":
      return "Sensor stale";
    case "iob":
      return "Insulin on board";
    case "temp_basal":
      return "Temp basal";
    case "sensitivity_ratio":
      return "Sensitivity";
    case "cob":
      return "Carbs on board";
    case "time_since_last_carb":
      return "Time since last carb";
    case "time_since_last_bolus":
      return "Time since last bolus";
    case "reservoir":
      return "Reservoir";
    case "site_age":
      return "Site age";
    case "sensor_age":
      return "Sensor age";
    case "tracker_age":
      return "Tracker age";
    case "pump_battery":
      return "Pump battery";
    case "uploader_battery":
      return "Phone battery";
    case "loop_stale":
      return "Loop has stopped";
    case "loop_enaction_stale":
      return "Loop not enacting";
    case "signal_loss":
      return "Signal loss";
    case "pump_suspended":
      return "Pump suspended";
    case "override_active":
      return "Override active";
    case "do_not_disturb":
      return "Do not disturb";
    case "pump_state":
      return "Pump mode";
    case "state_span_active":
      return "State active";
    case "sleep_session_active":
      return "Sleep session";
    case "time_of_day":
      return "Time of day";
    case "day_of_week":
      return "Day of week";
    case "alert_state":
      return "Other rule state";
    default: {
      const unhandled: never = kind;
      return unhandled;
    }
  }
}

export function factDescription(kind: LeafKind): string {
  switch (kind) {
    case "threshold":
      return "Current CGM reading vs. a threshold";
    case "glucose_bucket":
      return "Current reading falls in selected buckets (very-low … very-high)";
    case "predicted":
      return "AAPS or oref forecast crossing a threshold";
    case "rate_of_change":
      return "Glucose rising or falling faster than a rate";
    case "trend":
      return "Coarse direction bucket (rising fast, falling, …)";
    case "staleness":
      return "No CGM reading for N minutes";
    case "iob":
      return "Units of insulin still active";
    case "temp_basal":
      return "Active temp basal rate or % of scheduled";
    case "sensitivity_ratio":
      return "Autosens ratio (AAPS / Trio only)";
    case "cob":
      return "Grams of carbs still being absorbed";
    case "time_since_last_carb":
      return "Minutes elapsed since the last recorded carb entry";
    case "time_since_last_bolus":
      return "Minutes elapsed since the last recorded bolus";
    case "reservoir":
      return "Pump reservoir level (units)";
    case "site_age":
      return "Hours since last infusion site change";
    case "sensor_age":
      return "Days since CGM sensor start";
    case "tracker_age":
      return "Time since a tracker's start (Duration) or scheduled time (Event), negative before the event. A tracker with no active instance never matches";
    case "pump_battery":
      return "Pump battery percent";
    case "uploader_battery":
      return "Uploader phone battery percent";
    case "loop_stale":
      return "No APS cycle for N minutes";
    case "loop_enaction_stale":
      return "No enacted cycle for N minutes (closed loop)";
    case "signal_loss":
      return "No CGM data received for N minutes";
    case "pump_suspended":
      return "Pump suspension state";
    case "override_active":
      return "An override profile is active";
    case "do_not_disturb":
      return "Tenant DND state (manual or scheduled)";
    case "pump_state":
      return "Pump operational mode (automatic, manual, boost, …)";
    case "state_span_active":
      return "Generic state span (override, sleep, exercise, …)";
    case "sleep_session_active":
      return "A recorded sleep session is in progress";
    case "time_of_day":
      return "Current local time falls in a window";
    case "day_of_week":
      return "Current local day matches selected weekdays";
    case "alert_state":
      return "Reference another alert rule's state";
    default: {
      const unhandled: never = kind;
      return unhandled;
    }
  }
}

export function factGroupLabel(group: FactGroup): string {
  switch (group) {
    case "glucose":
      return "Glucose";
    case "insulin":
      return "Insulin";
    case "carbs":
      return "Carbs";
    case "device":
      return "Device";
    case "behaviour":
      return "Behavior";
    case "state":
      return "State spans";
    case "time":
      return "Time";
    case "alert":
      return "Alerts";
    default: {
      const unhandled: never = group;
      return unhandled;
    }
  }
}
