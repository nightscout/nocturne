import { EventAnchorKind, type EventCohortDefinition } from "$lib/api";
import { bg, bgDelta, bgLabel, time } from "$lib/utils/formatting";
import type { EventMetricKey, MetricKind } from "./event-response-view";
import { atMinute } from "./daily-profile-view";

/** Built per call so wuchale reads them in the current locale. */
export function anchorLabels(): Record<EventAnchorKind, string> {
  return {
    [EventAnchorKind.Meal]: "Meals",
    [EventAnchorKind.CorrectionBolus]: "Correction boluses",
    [EventAnchorKind.SensorStart]: "Sensor starts",
    [EventAnchorKind.ExerciseStart]: "Exercise starts",
    [EventAnchorKind.OverrideStart]: "Override starts",
    [EventAnchorKind.TemporaryTargetStart]: "Temporary target starts",
    [EventAnchorKind.UnannouncedRise]: "Unannounced rises",
  };
}

export function anchorLabel(kind: EventAnchorKind | undefined): string {
  return kind ? anchorLabels()[kind] : "";
}

/**
 * A cohort as the response applied it, told from every other by its number and its filters as well
 * as its colour: "Cohort 1: Meals 05:00–10:00, 30–80 g".
 */
export function cohortLabel(cohort: EventCohortDefinition): string {
  const parts = [anchorLabel(cohort.anchor)];
  if (cohort.fromMinute != null || cohort.toMinute != null)
    parts.push(`${time(atMinute(cohort.fromMinute ?? 0))}–${time(atMinute(cohort.toMinute ?? 0))}`);
  const filters = [parts.join(" ")];
  if (cohort.minGrams != null && cohort.maxGrams != null) filters.push(`${cohort.minGrams}–${cohort.maxGrams} g`);
  else if (cohort.minGrams != null) filters.push(`at least ${cohort.minGrams} g`);
  else if (cohort.maxGrams != null) filters.push(`at most ${cohort.maxGrams} g`);
  return `Cohort ${cohort.cohort}: ${filters.join(", ")}`;
}

/** One event's anchor, in the event list. */
export function anchorEventLabel(kind: EventAnchorKind | undefined): string {
  switch (kind) {
    case EventAnchorKind.Meal:
      return "Meal";
    case EventAnchorKind.CorrectionBolus:
      return "Correction bolus";
    case EventAnchorKind.SensorStart:
      return "Sensor start";
    case EventAnchorKind.ExerciseStart:
      return "Exercise";
    case EventAnchorKind.OverrideStart:
      return "Override";
    case EventAnchorKind.TemporaryTargetStart:
      return "Temporary target";
    case EventAnchorKind.UnannouncedRise:
      return "Unannounced rise";
    default:
      return "";
  }
}

export interface MetricDefinition {
  key: EventMetricKey;
  label: string;
  kind: MetricKind;
}

/**
 * Every figure of an event, in the order the table and the list show them.
 * @param backToStartMgdl The response's band around the start glucose that counts as back to start.
 */
export function metricDefinitions(backToStartMgdl?: number): MetricDefinition[] {
  return [
    { key: "startGlucose", label: "Start glucose", kind: "glucose" },
    { key: "peakRise", label: "Peak rise", kind: "glucoseChange" },
    { key: "minutesToPeak", label: "Time to peak", kind: "minutes" },
    { key: "glucoseAt2Hours", label: "Glucose at 2 h", kind: "glucose" },
    { key: "glucoseAt4Hours", label: "Glucose at 4 h", kind: "glucose" },
    {
      key: "minutesBackToStart",
      label:
        backToStartMgdl == null ? "Back to start" : `Back within ${bg(backToStartMgdl)} ${bgLabel()} of start`,
      kind: "minutes",
    },
    { key: "drop", label: "Drop to window end", kind: "glucoseDrop" },
    { key: "nadir", label: "Lowest glucose", kind: "glucose" },
    { key: "minutesToNadir", label: "Time to lowest glucose", kind: "minutes" },
    { key: "totalInsulin", label: "Total insulin in window", kind: "units" },
    { key: "netInsulin", label: "Net insulin above schedule in window", kind: "units" },
    { key: "netInsulinFirst30", label: "Above schedule, first 30 min", kind: "units" },
    { key: "netInsulinFirst60", label: "Above schedule, first 60 min", kind: "units" },
    { key: "netInsulinFirst120", label: "Above schedule, first 2 h", kind: "units" },
    { key: "iobAtAnchor", label: "IOB at the event", kind: "units" },
    { key: "iobAtEnd", label: "IOB at window end", kind: "units" },
    { key: "gramsPerUnit", label: "Observed grams per unit", kind: "gramsPerUnit" },
    { key: "dropPerUnit", label: "Drop per unit above schedule", kind: "glucosePerUnit" },
  ];
}

/** Minutes from the event, signed, as the relative axis and its tooltip read them. */
export function relativeMinutes(minute: number): string {
  if (minute === 0) return "Event";
  return minute > 0 ? `+${minute} min` : `−${-minute} min`;
}

/** A figure in its kind's display units, unit included. */
export function formatMetric(kind: MetricKind, value: number | null | undefined): string {
  if (value == null) return "–";
  switch (kind) {
    case "glucose":
      return `${bg(value)} ${bgLabel()}`;
    case "glucoseChange":
      return `${bgDelta(value)} ${bgLabel()}`;
    case "glucoseDrop":
      return `${bgDelta(value, false)} ${bgLabel()}`;
    case "minutes":
      return `${Math.round(value)} min`;
    case "units":
      return `${value.toFixed(2)} U`;
    case "gramsPerUnit":
      return `${value.toFixed(1)} g/U`;
    case "glucosePerUnit":
      return `${bgDelta(value, false)} ${bgLabel()}/U`;
  }
}

/** A whole-hour tick of the relative axis. */
export function relativeHours(minute: number): string {
  if (minute === 0) return "0";
  const hours = minute / 60;
  return hours > 0 ? `+${hours} h` : `−${-hours} h`;
}
