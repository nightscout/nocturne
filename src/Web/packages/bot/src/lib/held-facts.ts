import type { HeldFact } from "@nocturne/api-client";

/**
 * The words for a fact and a reason the engine names a held rule's missing data with. A value
 * this build does not know is shown as the engine sent it.
 */
const FACT_NAMES: Record<string, string> = {
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
};

const REASONS: Record<string, string> = {
  absent: "Missing",
  stale: "Too old",
  ahead_of_clock: "Has a time in the future",
  unrecognised: "Has a value that Nocturne does not know",
  inexact: "Not exact",
};

export function heldFactName(fact: string | undefined): string {
  return fact === undefined ? "" : (FACT_NAMES[fact] ?? fact);
}

export function heldReason(reason: string | undefined): string {
  return reason === undefined ? "" : (REASONS[reason] ?? reason);
}

export const describeHeldFacts = (facts: HeldFact[] | undefined) =>
  (facts ?? []).map((f) => ({ label: heldFactName(f.fact), value: heldReason(f.reason) }));
