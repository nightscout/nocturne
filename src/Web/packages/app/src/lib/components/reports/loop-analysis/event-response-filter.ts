import {
  EventAnchorKind,
  type EventCohortDefinition,
  type EventResponseDefinition,
  type EventResponseLimits,
  type EventUnavailableAnchor,
} from "#lib/api/index.js";
import type { getEventResponse } from "#lib/api/generated/loopAnalysis.generated.remote.js";
import type { ReadonlyURLSearchParams } from "$app/state";

type EventResponseQuery = Parameters<typeof getEventResponse>[0];

export const ANCHOR_KINDS: EventAnchorKind[] = Object.values(EventAnchorKind);

/** Each cohort parameter, suffixed with the cohort's number as the API binds it: anchor1 to anchor3. */
const COHORT_FIELDS = ["anchor", "fromMinute", "toMinute", "minGrams", "maxGrams"] as const;
const COHORT_NUMBERS = [1, 2, 3] as const;
/** The options' query parameters, as a response's unavailable filters name them. */
export const ISOLATED_ONLY = "isolatedOnly";
export const REQUIRE_CORROBORATION = "requireCorroboration";
const OPTION_PARAMS = [ISOLATED_ONLY, REQUIRE_CORROBORATION] as const;
const WINDOW_PARAMS = ["minutesBefore", "minutesAfter"] as const;

/** Every query parameter the events builder owns; a write clears them all before setting its own. */
const EVENT_PARAM_NAMES = [
  ...COHORT_NUMBERS.flatMap((n) => COHORT_FIELDS.map((field) => `${field}${n}`)),
  ...OPTION_PARAMS,
  ...WINDOW_PARAMS,
];

/**
 * The cohorts and window as the URL carries them, in the API's own definition and parameter names.
 * No cohorts means the API's default, which the response's `definition` then states; so does an
 * absent window.
 */
export type EventResponseState = EventResponseDefinition;

function toAnchor(value: string | null): EventAnchorKind | null {
  return ANCHOR_KINDS.find((kind) => kind === value) ?? null;
}

function toNumber(value: string | null): number | undefined {
  if (value == null || value === "") return undefined;
  const n = Number(value);
  return Number.isFinite(n) ? n : undefined;
}

function toWhole(value: string | null): number | undefined {
  const n = toNumber(value);
  return n != null && Number.isInteger(n) ? n : undefined;
}

export function readEventResponse(search: ReadonlyURLSearchParams): EventResponseState {
  const cohorts: EventCohortDefinition[] = [];
  for (const n of COHORT_NUMBERS) {
    const anchor = toAnchor(search.get(`anchor${n}`));
    if (!anchor) continue;
    cohorts.push({
      cohort: cohorts.length + 1,
      anchor,
      fromMinute: toNumber(search.get(`fromMinute${n}`)),
      toMinute: toNumber(search.get(`toMinute${n}`)),
      minGrams: anchor === EventAnchorKind.Meal ? toNumber(search.get(`minGrams${n}`)) : undefined,
      maxGrams: anchor === EventAnchorKind.Meal ? toNumber(search.get(`maxGrams${n}`)) : undefined,
    });
  }
  return {
    cohorts,
    isolatedOnly: search.get("isolatedOnly") === "true",
    requireCorroboration: search.get("requireCorroboration") === "true",
    minutesBefore: toWhole(search.get("minutesBefore")),
    minutesAfter: toWhole(search.get("minutesAfter")),
  };
}

/** The search params with the builder written into them, every other parameter left as it was. */
export function writeEventResponse(state: EventResponseState, search: ReadonlyURLSearchParams): URLSearchParams {
  const next = new URLSearchParams(search.toString());
  for (const name of EVENT_PARAM_NAMES) next.delete(name);
  for (const [name, value] of Object.entries(eventParameters(state))) next.set(name, String(value));
  return next;
}

/** Whether a cohort's window starts and ends at the same minute, which the builder does not send. */
export function sameTimes(cohort: EventCohortDefinition): boolean {
  return cohort.fromMinute != null && cohort.fromMinute === cohort.toMinute;
}

/**
 * A cohort as the API accepts it: grams within the response's limits and the right way round, a
 * window whose start and end are the same minute dropped, and no grams on a cohort that is not meals.
 */
export function normalisedCohort(cohort: EventCohortDefinition, maxGrams: number): EventCohortDefinition {
  const clamp = (g: number | undefined) => (g == null ? undefined : Math.min(maxGrams, Math.max(0, g)));
  let min = clamp(cohort.minGrams);
  let max = clamp(cohort.maxGrams);
  if (min != null && max != null && min > max) [min, max] = [max, min];
  const meal = cohort.anchor === EventAnchorKind.Meal;
  const allDay = sameTimes(cohort);
  return {
    ...cohort,
    fromMinute: allDay ? undefined : cohort.fromMinute,
    toMinute: allDay ? undefined : cohort.toMinute,
    minGrams: meal ? min : undefined,
    maxGrams: meal ? max : undefined,
  };
}

/** The builder's parameters, cohorts numbered in order; only what is set. */
function eventParameters(state: EventResponseState): Record<string, string | number | boolean> {
  const params: Record<string, string | number | boolean> = {};
  (state.cohorts ?? []).slice(0, COHORT_NUMBERS.length).forEach((cohort, i) => {
    if (!cohort.anchor) return;
    const n = i + 1;
    params[`anchor${n}`] = cohort.anchor;
    if (cohort.fromMinute != null) params[`fromMinute${n}`] = cohort.fromMinute;
    if (cohort.toMinute != null) params[`toMinute${n}`] = cohort.toMinute;
    if (cohort.anchor === EventAnchorKind.Meal) {
      if (cohort.minGrams != null) params[`minGrams${n}`] = cohort.minGrams;
      if (cohort.maxGrams != null) params[`maxGrams${n}`] = cohort.maxGrams;
    }
  });
  if (state.isolatedOnly) params.isolatedOnly = true;
  if (state.requireCorroboration) params.requireCorroboration = true;
  if (state.minutesBefore != null) params.minutesBefore = state.minutesBefore;
  if (state.minutesAfter != null) params.minutesAfter = state.minutesAfter;
  return params;
}

/** The event-response query: the shared filter's query with the builder's parameters. */
export function eventResponseQuery(
  base: Pick<EventResponseQuery, "startDate" | "endDate"> & Partial<EventResponseQuery>,
  state: EventResponseState
): EventResponseQuery {
  return { ...base, ...eventParameters(state) };
}

/**
 * The state without the cohorts whose anchor the caller cannot ask for and the options it cannot
 * use, or null when it holds none of them.
 */
export function withoutUnavailableEvents(
  state: EventResponseState,
  unavailableAnchors: readonly EventUnavailableAnchor[],
  unavailableFilters: readonly string[]
): EventResponseState | null {
  const anchors = new Set(unavailableAnchors.map((a) => a.anchor));
  const filters = new Set(unavailableFilters);
  const cohorts = (state.cohorts ?? []).filter((c) => !c.anchor || !anchors.has(c.anchor));
  const isolatedOnly = state.isolatedOnly && !filters.has(ISOLATED_ONLY);
  const requireCorroboration = state.requireCorroboration && !filters.has(REQUIRE_CORROBORATION);
  if (
    cohorts.length === (state.cohorts ?? []).length &&
    isolatedOnly === state.isolatedOnly &&
    requireCorroboration === state.requireCorroboration
  )
    return null;
  return { ...state, cohorts, isolatedOnly, requireCorroboration };
}

/**
 * The state without the window and grams the response's limits do not allow, which the API
 * ignored, with the parameter names it dropped as the API names them; or null when it holds none.
 */
export function withinLimits(
  state: EventResponseState,
  limits: EventResponseLimits
): { state: EventResponseState; dropped: string[] } | null {
  const dropped: string[] = [];
  const grams = (g: number | undefined, name: string) => {
    if (g == null || (g >= 0 && g <= (limits.maxGrams ?? 0))) return g;
    dropped.push(name);
    return undefined;
  };
  const minutes = (m: number | undefined, choices: number[] | undefined, name: string) => {
    if (m == null || (choices ?? []).includes(m)) return m;
    dropped.push(name);
    return undefined;
  };
  const next: EventResponseState = {
    ...state,
    minutesBefore: minutes(state.minutesBefore, limits.minutesBeforeChoices, "minutesBefore"),
    minutesAfter: minutes(state.minutesAfter, limits.minutesAfterChoices, "minutesAfter"),
    cohorts: (state.cohorts ?? []).map((c, i) => ({
      ...c,
      minGrams: grams(c.minGrams, `minGrams${i + 1}`),
      maxGrams: grams(c.maxGrams, `maxGrams${i + 1}`),
    })),
  };
  return dropped.length > 0 ? { state: next, dropped } : null;
}

/** `HH:mm` for a minute of the day, as a time input reads it. */
export function minuteToClock(minute: number | null | undefined): string {
  if (minute == null) return "";
  const m = ((minute % 1440) + 1440) % 1440;
  return `${String(Math.floor(m / 60)).padStart(2, "0")}:${String(m % 60).padStart(2, "0")}`;
}

/** The minute of the day a time input's `HH:mm` names, or undefined when empty. */
export function clockToMinute(value: string): number | undefined {
  const match = /^(\d{1,2}):(\d{2})$/.exec(value);
  return match ? Number(match[1]) * 60 + Number(match[2]) : undefined;
}
