import type { HeldFact } from "@nocturne/api-client";

/**
 * The words for a fact and a reason the engine names a held rule's missing data with. A value
 * this build does not know is shown as the engine sent it.
 */
export const FACT_NAMES: Record<string, string> = {
  glucose: "Glucose readings",
  predictions: "Glucose forecast",
  insulin: "Insulin data",
  carbs: "Carb data",
  pump: "Pump data",
  aps: "Loop data",
  uploader: "Uploader data",
  transmitter: "Transmitter data",
  state_spans: "Pump mode and override data",
  device_ages: "Site and sensor change data",
  trackers: "Tracker data",
  sleep: "Sleep data",
  active_alerts: "Alert data",
  do_not_disturb: "Do Not Disturb data",
  time_zone: "Time zone",
  sensor_state: "Sensor state",
  aps_determination: "Loop result",
  target_range: "Target range",
};

export const REASONS: Record<string, string> = {
  absent: "Missing",
  stale: "Too old",
  ahead_of_clock: "Has a time in the future",
  unrecognised: "Has a value that Nocturne does not know",
  inexact: "Not exact",
  sensor_not_ok: "Not OK, so Nocturne does not use the glucose value",
};

export function heldFactName(fact: string | undefined): string {
  return fact !== undefined && Object.hasOwn(FACT_NAMES, fact) ? FACT_NAMES[fact] : (fact ?? "");
}

export function heldReason(reason: string | undefined): string {
  return reason !== undefined && Object.hasOwn(REASONS, reason) ? REASONS[reason] : (reason ?? "");
}

export const describeHeldFacts = (facts: HeldFact[] | undefined) =>
  (facts ?? []).map((f) => ({ label: heldFactName(f.fact), value: heldReason(f.reason) }));
