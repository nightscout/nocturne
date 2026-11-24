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
    case "transmitter_battery":
      return "Transmitter battery";
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
    case "sensor_state":
      return "Sensor state";
    case "eventual_glucose":
      return "Eventual glucose";
    case "carbs_required":
      return "Carbs needed";
    default: {
      const unhandled: never = kind;
      return unhandled;
    }
  }
}

export function factDescription(kind: LeafKind): string {
  switch (kind) {
    case "threshold":
      return "The current glucose is above or below a level";
    case "glucose_bucket":
      return "The current glucose is in one of the ranges you select";
    case "predicted":
      return "The forecast from the loop goes above or below a level";
    case "rate_of_change":
      return "Glucose increases or decreases faster than a rate";
    case "trend":
      return "The direction of the trend arrow, such as falling or rising fast";
    case "staleness":
      return "The newest glucose reading is older than a number of minutes";
    case "iob":
      return "The units of insulin that are still active";
    case "temp_basal":
      return "The temp basal that is active, as a rate or a percentage of the schedule";
    case "sensitivity_ratio":
      return "The sensitivity ratio from AndroidAPS or Trio";
    case "cob":
      return "The grams of carbs that are not absorbed yet";
    case "time_since_last_carb":
      return "The minutes since the last carb entry";
    case "time_since_last_bolus":
      return "The minutes since the last bolus";
    case "reservoir":
      return "The units of insulin left in the pump";
    case "site_age":
      return "The hours since the last infusion site change";
    case "sensor_age":
      return "The days since the sensor started";
    case "tracker_age":
      return "Time since a tracker's start (Duration) or scheduled time (Event), negative before the event. A tracker with no active instance never matches";
    case "pump_battery":
      return "The battery level of the pump";
    case "uploader_battery":
      return "The battery level of the phone that uploads the data";
    case "transmitter_battery":
      return "The voltage, resistance or age of the CGM transmitter (xDrip+ only)";
    case "loop_stale":
      return "The loop did not run for a number of minutes";
    case "loop_enaction_stale":
      return "The loop did not change the insulin delivery for a number of minutes";
    case "signal_loss":
      return "No glucose data arrived for a number of minutes";
    case "pump_suspended":
      return "The pump is suspended, or is not";
    case "override_active":
      return "An override is active, or is not";
    case "do_not_disturb":
      return "Do not disturb is on, or is not";
    case "pump_state":
      return "The pump is in a mode such as automatic, manual or boost";
    case "state_span_active":
      return "A state such as an override, exercise or illness is active";
    case "sleep_session_active":
      return "A recorded sleep session is in progress";
    case "time_of_day":
      return "The time is between two times of day";
    case "day_of_week":
      return "The day is one of the weekdays you select";
    case "alert_state":
      return "A different alert is active, or acknowledged";
    case "sensor_state":
      return "The CGM sensor reports a state, for example warm-up or an error";
    case "eventual_glucose":
      return "The glucose that the loop expects at the end of its forecast is above or below a level";
    case "carbs_required":
      return "The grams of carbs that the loop says are necessary to prevent a low";
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
