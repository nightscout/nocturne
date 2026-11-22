import type { AlertHold } from "@nocturne/api-client";
import { formatDateTimeCompact } from "#lib/utils/formatting.js";

/** The facts the engine names a held rule's missing data by. */
type KnownFact =
  | "glucose"
  | "predictions"
  | "insulin"
  | "carbs"
  | "pump"
  | "aps"
  | "uploader"
  | "transmitter"
  | "state_spans"
  | "device_ages"
  | "trackers"
  | "sleep"
  | "active_alerts"
  | "do_not_disturb"
  | "time_zone";

type KnownReason = "absent" | "stale" | "ahead_of_clock" | "unrecognised" | "inexact";

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
    default: {
      const unhandled: never = reason;
      return unhandled;
    }
  }
}

const KNOWN_FACTS: ReadonlySet<string> = new Set<KnownFact>([
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
]);

const KNOWN_REASONS: ReadonlySet<string> = new Set<KnownReason>([
  "absent",
  "stale",
  "ahead_of_clock",
  "unrecognised",
  "inexact",
]);

const isKnownFact = (fact: string): fact is KnownFact => KNOWN_FACTS.has(fact);
const isKnownReason = (reason: string): reason is KnownReason => KNOWN_REASONS.has(reason);

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

/** "Waiting for data since …". */
export function holdSinceText(hold: AlertHold): string {
  return hold.since
    ? `Waiting for data since ${formatDateTimeCompact(hold.since)}.`
    : "Waiting for data.";
}

export function heldOpenText(): string {
  return "This alert stays open while its data is missing. It closes after 24 hours of missing data.";
}

/** The words for why a closed alert closed, where the reason needs them; null otherwise. */
export function resolutionReasonText(reason: string | undefined): string | null {
  return reason === "hold-bound" ? "Closed because its data was missing for 24 hours." : null;
}
