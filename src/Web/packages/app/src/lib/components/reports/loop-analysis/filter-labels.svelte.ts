import type { MaskKey } from "./loop-analysis-filter";

/** Built per call so wuchale reads them in the current locale. */
export function maskLabels(): Record<MaskKey, string> {
  return {
    excludeOverrides: "Overrides",
    excludeTemporaryTargets: "Temporary targets",
    excludeExercise: "Exercise",
    excludeIllness: "Illness",
    excludeOpenLoop: "Time not in closed loop",
  };
}

/** Why a filter is unavailable, by the read scope it needs. */
export function missingScopeReason(scope: string): string {
  switch (scope) {
    case "treatments.read":
      return "Needs access to treatments";
    case "devices.read":
      return "Needs access to devices";
    case "therapy.read":
      return "Needs access to therapy settings";
    default:
      return "Not available with your access";
  }
}

/** A name from a response's `ignoredFilters`, as the filter bar and the event builder label it. */
export function ignoredFilterLabel(name: string): string {
  if (name === "profileName") return "Profile";
  const cohort = /^anchor(\d)$/.exec(name);
  if (cohort) return `Cohort ${cohort[1]}`;
  const grams = /^(min|max)Grams(\d)$/.exec(name);
  if (grams) return grams[1] === "min" ? `Cohort ${grams[2]}, at least (g)` : `Cohort ${grams[2]}, at most (g)`;
  const labels: Record<string, string> = { ...eventOptionLabels(), ...eventWindowLabels(), ...maskLabels() };
  return labels[name] ?? name;
}

/** The Meals and events options, by query parameter, as the builder and the notices name them. */
export function eventOptionLabels(): Record<"isolatedOnly" | "requireCorroboration", string> {
  return {
    isolatedOnly: "Isolated events only",
    requireCorroboration: "Only rises with extra loop insulin",
  };
}

/** The Meals and events window, by query parameter, as the builder names it. */
export function eventWindowLabels(): Record<"minutesBefore" | "minutesAfter", string> {
  return {
    minutesBefore: "Before the event",
    minutesAfter: "After the event",
  };
}
