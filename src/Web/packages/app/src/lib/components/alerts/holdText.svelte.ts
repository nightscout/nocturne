import type { AlertHold } from "@nocturne/api-client";
import { formatDateTimeCompact } from "#lib/utils/formatting.js";

/** The facts the engine names a held rule's missing data by. */
export const KNOWN_FACTS = [
  "glucose",
  "predictions",
  "insulin",
  "carbs",
  "pump",
  "aps",
  "uploader",
  "transmitter",
  "state_spans",
  "device_ages",
  "trackers",
  "sleep",
  "active_alerts",
  "do_not_disturb",
  "time_zone",
  "sensor_state",
  "aps_determination",
  "target_range",
] as const;

export const KNOWN_REASONS = ["absent", "stale", "ahead_of_clock", "unrecognised", "inexact", "sensor_not_ok"] as const;

type KnownFact = (typeof KNOWN_FACTS)[number];
type KnownReason = (typeof KNOWN_REASONS)[number];

function factName(fact: KnownFact): string {
  switch (fact) {
    case "glucose":
      return "Glucose readings";
    case "predictions":
      return "Glucose forecast";
    case "insulin":
      return "Insulin data";
    case "carbs":
      return "Carb data";
    case "pump":
      return "Pump data";
    case "aps":
      return "Loop data";
    case "uploader":
      return "Uploader data";
    case "transmitter":
      return "Transmitter data";
    case "state_spans":
      return "Pump mode and override data";
    case "device_ages":
      return "Site and sensor change data";
    case "trackers":
      return "Tracker data";
    case "sleep":
      return "Sleep data";
    case "active_alerts":
      return "Alert data";
    case "do_not_disturb":
      return "Do Not Disturb data";
    case "time_zone":
      return "Time zone";
    case "sensor_state":
      return "Sensor state";
    case "aps_determination":
      return "Loop result";
    case "target_range":
      return "Target range";
    default: {
      const unhandled: never = fact;
      return unhandled;
    }
  }
}

function reasonText(reason: KnownReason): string {
  switch (reason) {
    case "absent":
      return "missing";
    case "stale":
      return "too old";
    case "ahead_of_clock":
      return "has a time in the future";
    case "unrecognised":
      return "has a value that Nocturne does not know";
    case "inexact":
      return "not exact";
    case "sensor_not_ok":
      return "not OK, so Nocturne does not use the glucose value";
    default: {
      const unhandled: never = reason;
      return unhandled;
    }
  }
}

const isKnownFact = (fact: string): fact is KnownFact => KNOWN_FACTS.some((known) => known === fact);
const isKnownReason = (reason: string): reason is KnownReason => KNOWN_REASONS.some((known) => known === reason);

/** A fact by name; one this build does not know, as the API sent it. */
export function heldFactName(fact: string | undefined): string {
  if (fact === undefined) return "";
  return isKnownFact(fact) ? factName(fact) : fact;
}

/** Why a fact is unknown; a reason this build does not know, as the API sent it. */
export function heldReason(reason: string | undefined): string {
  if (reason === undefined) return "";
  return isKnownReason(reason) ? reasonText(reason) : reason;
}

/** Each fact the hold waits for, as "Glucose readings: too old". */
export function holdFactLines(hold: AlertHold): string[] {
  return (hold.facts ?? []).map((f) => `${heldFactName(f.fact)}: ${heldReason(f.reason)}`);
}

/** "Waiting for usable data since …". */
export function holdSinceText(hold: AlertHold): string {
  return hold.since
    ? `Waiting for usable data since ${formatDateTimeCompact(hold.since)}.`
    : "Waiting for usable data.";
}

export function heldOpenText(): string {
  return "This alert stays open while it has no usable data. It closes after 24 hours with no usable data.";
}

/** The words for why a closed alert closed, where the reason needs them; null otherwise. */
export function resolutionReasonText(reason: string | undefined): string | null {
  return reason === "hold-bound" ? "Closed because it had no usable data for 24 hours." : null;
}
