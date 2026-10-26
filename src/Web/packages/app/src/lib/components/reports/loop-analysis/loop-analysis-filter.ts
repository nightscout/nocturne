import { DayOfWeek, LoopAnalysisComparison } from "#lib/api/index.js";
import type { getDailyProfile } from "#lib/api/generated/loopAnalysis.generated.remote.js";
import { isDayString } from "#lib/utils/date-range.js";
import type { ReadonlyURLSearchParams } from "$app/state";

type DailyProfileQuery = Parameters<typeof getDailyProfile>[0];

export type MaskKey =
  | "excludeOverrides"
  | "excludeTemporaryTargets"
  | "excludeExercise"
  | "excludeIllness"
  | "excludeOpenLoop";

export const MASK_KEYS: MaskKey[] = [
  "excludeOverrides",
  "excludeTemporaryTargets",
  "excludeExercise",
  "excludeIllness",
  "excludeOpenLoop",
];

/**
 * The report's filter as the URL carries it, beside the shared report range (`days`/`from`/`to`),
 * whose names it must not reuse: the weekdays travel as `weekdays`. A mask the URL leaves out is
 * absent here and is not sent, so the API's default applies and the response's `appliedFilters`
 * says what it was.
 */
export interface LoopAnalysisFilterState {
  compare: LoopAnalysisComparison;
  /** yyyy-MM-dd, inclusive; read only when {@link compare} is custom. */
  compareFrom: string | null;
  compareTo: string | null;
  weekdays: DayOfWeek[];
  profileName: string | null;
  masks: Partial<Record<MaskKey, boolean>>;
}

/** Every query parameter this filter owns; a write clears them all before setting its own. */
const FILTER_PARAM_NAMES = [
  "compare",
  "compareFrom",
  "compareTo",
  "weekdays",
  "profile",
  ...MASK_KEYS,
];

const COMPARISONS: Record<string, LoopAnalysisComparison> = {
  previous: LoopAnalysisComparison.Previous,
  custom: LoopAnalysisComparison.Custom,
};

export function toComparison(value: string): LoopAnalysisComparison | null {
  return Object.values(LoopAnalysisComparison).find((c) => c === value) ?? null;
}

const WEEKDAYS: DayOfWeek[] = [
  DayOfWeek.Sunday,
  DayOfWeek.Monday,
  DayOfWeek.Tuesday,
  DayOfWeek.Wednesday,
  DayOfWeek.Thursday,
  DayOfWeek.Friday,
  DayOfWeek.Saturday,
];

/** Weekdays from their numeric spelling, unknown ones dropped, each once, Sunday first. */
export function toWeekdays(values: readonly (string | number)[]): DayOfWeek[] {
  const picked = new Set(values.map(Number));
  return WEEKDAYS.filter((day) => picked.has(day));
}

export function readLoopAnalysisFilter(search: ReadonlyURLSearchParams): LoopAnalysisFilterState {
  const compareFrom = search.get("compareFrom");
  const compareTo = search.get("compareTo");

  const masks: Partial<Record<MaskKey, boolean>> = {};
  for (const key of MASK_KEYS) {
    const value = search.get(key);
    if (value === "true" || value === "false") masks[key] = value === "true";
  }

  return {
    compare: COMPARISONS[search.get("compare") ?? ""] ?? LoopAnalysisComparison.None,
    compareFrom: isDayString(compareFrom) ? compareFrom : null,
    compareTo: isDayString(compareTo) ? compareTo : null,
    weekdays: toWeekdays((search.get("weekdays") ?? "").split(",").filter((part) => part !== "")),
    profileName: search.get("profile") || null,
    masks,
  };
}

/**
 * The search params with this filter written into them, leaving every parameter it does not own
 * as it was.
 */
export function writeLoopAnalysisFilter(
  filter: LoopAnalysisFilterState,
  search: ReadonlyURLSearchParams
): URLSearchParams {
  const next = new URLSearchParams(search.toString());
  for (const name of FILTER_PARAM_NAMES) next.delete(name);

  if (filter.compare !== LoopAnalysisComparison.None) next.set("compare", filter.compare.toLowerCase());
  if (filter.compare === LoopAnalysisComparison.Custom) {
    if (filter.compareFrom) next.set("compareFrom", filter.compareFrom);
    if (filter.compareTo) next.set("compareTo", filter.compareTo);
  }
  if (filter.weekdays.length > 0) next.set("weekdays", toWeekdays(filter.weekdays).join(","));
  if (filter.profileName) next.set("profile", filter.profileName);
  for (const key of MASK_KEYS) {
    const value = filter.masks[key];
    if (value != null) next.set(key, String(value));
  }
  return next;
}

/** Why a custom comparison cannot be asked for yet, or null when it can. */
export function customComparisonProblem(filter: LoopAnalysisFilterState): "incomplete" | "reversed" | null {
  if (filter.compare !== LoopAnalysisComparison.Custom) return null;
  if (!filter.compareFrom || !filter.compareTo) return "incomplete";
  return filter.compareFrom > filter.compareTo ? "reversed" : null;
}

/**
 * The Daily profile query for an inclusive run of local days. Only what the URL sets is sent, so
 * every default stays the API's; a custom comparison that cannot be asked for yet is left out.
 */
export function dailyProfileQuery(
  filter: LoopAnalysisFilterState,
  range: { startDate: string; endDate: string }
): DailyProfileQuery {
  const query: DailyProfileQuery = { startDate: range.startDate, endDate: range.endDate };

  if (filter.compare === LoopAnalysisComparison.Previous) query.compare = filter.compare;
  if (
    filter.compare === LoopAnalysisComparison.Custom &&
    customComparisonProblem(filter) == null &&
    filter.compareFrom &&
    filter.compareTo
  ) {
    query.compare = filter.compare;
    query.compareStartDate = filter.compareFrom;
    query.compareEndDate = filter.compareTo;
  }
  if (filter.weekdays.length > 0) query.days = filter.weekdays;
  if (filter.profileName) query.profileName = filter.profileName;
  for (const key of MASK_KEYS) {
    const value = filter.masks[key];
    if (value != null) query[key] = value;
  }
  return query;
}

/**
 * The filter without the parts named in `unavailable` (a response's unavailable filters, by query
 * parameter), or null when it sets none of them.
 */
export function withoutUnavailable(
  filter: LoopAnalysisFilterState,
  unavailable: readonly string[]
): LoopAnalysisFilterState | null {
  const names = new Set(unavailable);
  const masks = { ...filter.masks };
  let changed = false;
  for (const key of MASK_KEYS) {
    if (names.has(key) && masks[key] != null) {
      delete masks[key];
      changed = true;
    }
  }
  const dropProfile = names.has("profileName") && filter.profileName != null;
  if (!changed && !dropProfile) return null;
  return { ...filter, masks, profileName: dropProfile ? null : filter.profileName };
}
