import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
import type { StarterGroup } from "./starters.js";
import {
  convertFromDisplayUnits,
  convertToDisplayUnits,
  type GlucoseUnits,
} from "@nocturne/ui/glucose";
import {
  AGAINST_GRID_MINUTES,
  ANCHOR_MEAL,
  ANCHOR_RISE,
  ALONGS,
  ALONG_TIME_OF_DAY,
  NO_UNIT,
  DAY_KINDS,
  OCCURRENCE_DATASET,
  OUTCOMES,
  OUTCOMES_DATASET,
  SLEEP_ANCHORS,
  SPREAD_DATASET,
  DAY_GROUP_PRESETS,
  DEFAULT_REPORT_HOURS,
  EVENT_AFTER_CHOICES,
  EVENT_BEFORE_CHOICES,
  EVENT_DATASET,
  EVENT_MINUTES_AFTER,
  EVENT_MINUTES_BEFORE,
  FAMILY_LANES,
  LEAVE_OUTS,
  RANGE_PRESETS,
  SIGNALS,
  WEEKDAY_IDS,
  anchor,
  cellOf,
  isStartAnchor,
  lensesOf,
  signal as signalOf,
  type Along,
  type AnchorId,
  type DayKind,
  type Cell,
  type EventShow,
  type Outcome,
  type OutcomeChoice,
  type WhenAxis,
  type CombineOp,
  type DayGroupPresetId,
  type Family,
  type LeaveOutId,
  type LensId,
  type MarkChoice,
  type RangePresetId,
  type SignalId,
  type Weekday,
} from "./vocabulary.js";
import { alongLabel, anchorPhrase, leaveOutLabel, lensLabel, markLabel, outcomeLabel, rangeLabel, signalLabel, weekdayName } from "./vocabulary-words.svelte.js";

export interface Relate {
  /** Rolling-mean window in minutes, or null for none. */
  smooth: number | null;
  /** Mean-and-standard-deviation window in minutes, or null for none. */
  sd: number | null;
  trend: boolean;
  /** The trend line's 95% confidence interval, drawn as a band. */
  confidence: boolean;
  /** Glucose under `event`: the change from each event's start. */
  fromStart: boolean;
}

/** A signal the member placed. Its mark and relate options are kept per lens. */
export interface PlacedSignal {
  id: SignalId;
  marks: Partial<Record<LensId, string>>;
  relate: Partial<Record<LensId, Relate>>;
}

/** Two placed signals combined slot by slot, under the lens it was made in. */
export interface Combo {
  key: number;
  lens: LensId;
  a: SignalId;
  b: SignalId;
  op: CombineOp;
  mark?: string;
}

/**
 * A lane is one track of the chart: the shared lane of a family, or one
 * signal's own.
 */
export interface Lane {
  id: string;
  family: Family;
  own: boolean;
  signals: SignalId[];
}

export interface FoodRef {
  id: string;
  name: string;
}

/**
 * A group of events under the `event` lens; becomes one cohort of the
 * `event_response` query.
 */
export interface Moment {
  key: number;
  what: AnchorId;
  timeOfDay?: { fromMinute: number; toMinute: number };
  weekdays?: Weekday[];
  grams?: { min?: number; max?: number };
  /**
   * An empty list is a food narrowing whose food is still to be picked; it is
   * not sent.
   */
  foods?: FoodRef[];
  foodCategory?: string;
  /** Minutes from the last carbs before the event to the event. */
  minutesSinceMeal?: Bounds;
  /** Glucose at the event, whole mg/dL: the spec's unit for every tenant. */
  startGlucose?: Bounds;
  /** Insulin on board at the event, U. */
  insulinOnBoard?: Bounds;
  /**
   * Every event of the anchor that no other group of that anchor takes in: the
   * API's `others` group. It takes no narrowing of its own.
   */
  everythingElse?: boolean;
}

/** An inclusive range; either end may be missing. */
export interface Bounds {
  min?: number;
  max?: number;
}

/** A condition on the state at the event, each sent under its own name. */
export type ConditionKind =
  | "minutesSinceMeal"
  | "startGlucose"
  | "insulinOnBoard";

export type MomentPatch = Partial<Omit<Moment, "key" | "everythingElse">>;

export interface DateSpan {
  /** A local date, yyyy-MM-dd. */
  from: string;
  /** A local date, yyyy-MM-dd, counted. */
  to: string;
}

/** What a day had, as the `days` parameter takes it. */
export interface DayFilter {
  kind: DayKind;
  minCarbs?: number;
  maxCarbs?: number;
}

/** Which event figure applies to which anchors: the catalogue's `x-eventFigures`. */
export type EventFigures = Record<string, readonly string[]>;

/** The letters of the groups of events, A to F. */
export const GROUP_LETTERS = ["A", "B", "C", "D", "E", "F"];

/**
 * The most groups of events drawn over one another. Overlaid bands of more groups cannot be told
 * apart however distinct their colours, so from one more the chart is a grid of small charts, one for
 * each group. A rule of reading, not of colour: the engine has more group hues than this.
 */
export const MAX_OVERLAID_GROUPS = 3;

export interface EventOptions {
  before: number;
  after: number;
  isolatedOnly: boolean;
  requireCorroboration: boolean;
  comparison: DateSpan | null;
  /** The curve over time, or the events one by one. */
  show: EventShow;
  /** Each event: the figure up, and what it lies along. */
  up: Outcome;
  along: Along;
  /** Each event: the least-squares line through the points. */
  fit: boolean;
  /** Spread: the figure counted. */
  figure: Outcome;
  /** When: by hour of day or by weekday. */
  when: WhenAxis;
}

export interface WorkspaceState {
  title: string;
  /** The chart's own range in hours, or null for the report's. */
  range: number | null;
  lens: LensId;
  placed: PlacedSignal[];
  lanes: Lane[];
  combos: Combo[];
  moments: Moment[];
  dayGroups: Weekday[][];
  /** Days by what happened: those days against all other days, in place of the weekday groups. */
  dayFilter: DayFilter | null;
  findLag: boolean;
  leaveOut: Record<LeaveOutId, boolean>;
  /** Only time this profile was active, or null for all time. */
  profileName: string | null;
  event: EventOptions;
}

/** A preset starting state. */
export interface Starter {
  id: string;
  /** The heading the starter list puts it under. */
  group: StarterGroup;
  lens: LensId;
  rangeHours: number;
  signals: { id: SignalId; relate?: Partial<Relate>; mark?: string }[];
  moments?: Omit<Moment, "key">[];
  dayGroups?: Weekday[][];
  dayFilter?: DayFilter;
  findLag?: boolean;
  event?: Partial<EventOptions>;
}

export type CaptionKind = "signals" | "lens" | "range" | "groups" | "options";

export interface CaptionPart {
  kind: CaptionKind;
  text: string;
}

/**
 * Where a moment's events land in the result: a layer, and the cohort (from 1)
 * inside it.
 */
export interface CohortRef {
  layer: string;
  cohort: number;
}

/** A control's state before it is used: why it cannot be used now, or null. */
export type Reason = string | null;

export interface SignalOption {
  id: SignalId;
  placed: boolean;
  /** Why the signal cannot be placed now; null when it can, or is placed. */
  reason: Reason;
}

/** A signal another lens draws: choosing it switches to that lens and places it. */
export interface ElsewhereOption {
  id: SignalId;
  lens: LensId;
  placed: boolean;
  reason: Reason;
}

export interface RangeOption {
  id: RangePresetId;
  label: string;
  hours: number;
  reason: Reason;
}

/**
 * What the spec builder produced, with the spec layer ids each primitive
 * became.
 */
export interface Built {
  spec: Record<string, unknown> | null;
  panels: Record<string, unknown>[];
  signalLayers: Partial<Record<SignalId, string[]>>;
  comboLayers: Record<number, string[]>;
  momentCohorts: CohortRef[][];
  dayGroupLayers: string[][];
  tracks: number;
  layers: number;
  /** One key for each dataset query, as the run cache counts reads. */
  reads: string[];
  /** The range the chart was built for. */
  hours: number;
}

/** What the report gives a chart: its range, and the room left in the report. */
export interface WorkspaceHost {
  reportHours(): number;
  /** Why the chart, built as given, does not fit the report; null when it does. */
  budget(built: Built): Reason;
}

export const MAX_DAY_GROUPS = 3;
const MAX_MOMENTS_FALLBACK = 3;
/**
 * The API folds a time axis into at most this many slots, so a window must fit
 * the coarsened step.
 */
const MAX_SLOTS = 2000;
const NICE_GRIDS = [5, 10, 15, 20, 30, 60, 120, 240];
/** The second axis of a track: a track has at most two units. */
const UNITS_PER_TRACK = 2;
const OVERFLOW_HEIGHT = 0.75;

const NO_RELATE: Relate = {
  smooth: null,
  sd: null,
  trend: false,
  confidence: false,
  fromStart: false,
};

const FALLBACK_LEAVE_OUT: Record<LeaveOutId, boolean> = {
  overrides: false,
  temporaryTargets: false,
  exercise: false,
  illness: true,
  openLoop: true,
};

const STANDALONE: WorkspaceHost = {
  reportHours: () => DEFAULT_REPORT_HOURS,
  budget: () => null,
};

let nextKey = 0;

/** A key unique in the page, for blocks, moments and combinations. */
export function freshKey(): number {
  return nextKey++;
}

/** Keeps `freshKey` from giving out a key a restored report already holds. */
export function reserveKey(key: number) {
  nextKey = Math.max(nextKey, key + 1);
}

/** The API's defaults for leaving time out, from the catalogue when it gives them. */
export function leaveOutDefaults(
  catalogue: CustomReportCatalogue
): Record<LeaveOutId, boolean> {
  const properties: Record<string, { default?: unknown }> | undefined =
    catalogue.datasets.find((d) => d.name === EVENT_DATASET)?.parameters
      ?.properties;
  const value = (id: LeaveOutId): boolean => {
    const l = LEAVE_OUTS.find((x) => x.id === id)!;
    const given = properties?.[l.parameter]?.default;
    return typeof given === "boolean" ? given : l.fallback;
  };
  return {
    overrides: value("overrides"),
    temporaryTargets: value("temporaryTargets"),
    exercise: value("exercise"),
    illness: value("illness"),
    openLoop: value("openLoop"),
  };
}

/** The catalogue's `x-eventFigures.figures`: each figure with the anchors it applies to. */
export function eventFiguresOf(catalogue: CustomReportCatalogue): EventFigures {
  const params: unknown = catalogue.datasets.find((d) => d.name === EVENT_DATASET)?.parameters;
  const figures = objectAt(objectAt(params, "x-eventFigures"), "figures") ?? {};
  const out: EventFigures = {};
  for (const [key, value] of Object.entries(figures))
    if (Array.isArray(value))
      out[key] = value.filter((v: unknown): v is string => typeof v === "string");
  return out;
}

function choicesOf(
  catalogue: CustomReportCatalogue,
  parameter: string,
  fallback: number[]
): number[] {
  const given: unknown = catalogue.datasets.find(
    (d) => d.name === EVENT_DATASET
  )?.parameters?.properties?.[parameter]?.enum;
  return Array.isArray(given) && given.every((v) => typeof v === "number")
    ? given
    : fallback;
}

export function emptyState(
  defaults: Record<LeaveOutId, boolean>
): WorkspaceState {
  return {
    title: "",
    range: null,
    lens: "timeline",
    placed: [],
    lanes: [],
    combos: [],
    moments: [{ key: freshKey(), what: ANCHOR_MEAL }],
    dayGroups: DAY_GROUP_PRESETS.everyDay.groups.map((g) => [...g]),
    dayFilter: null,
    findLag: false,
    leaveOut: { ...defaults },
    profileName: null,
    event: {
      before: EVENT_MINUTES_BEFORE,
      after: EVENT_MINUTES_AFTER,
      isolatedOnly: false,
      requireCorroboration: false,
      comparison: null,
      show: "curve",
      up: "peakRise",
      along: "startGlucose",
      fit: true,
      figure: "peakRise",
      when: "hour",
    },
  };
}

function relateOf(placed: PlacedSignal, lens: LensId): Relate {
  return { ...NO_RELATE, ...placed.relate[lens] };
}

function markChoices(placed: PlacedSignal, lens: LensId, cell: Cell) {
  return relateOf(placed, lens).fromStart && cell.fromStartMarks
    ? cell.fromStartMarks
    : cell.marks;
}

function markOf(
  placed: PlacedSignal,
  lens: LensId,
  cell: Cell
): MarkChoice | undefined {
  const choices = markChoices(placed, lens, cell);
  return choices.find((m) => m.mark === placed.marks[lens]) ?? choices[0];
}

/**
 * The signals drawn under the state's lens, in placement order: under
 * `against`, the first two.
 */
export function shownSignals(state: WorkspaceState): SignalId[] {
  const available = state.placed
    .map((p) => p.id)
    .filter((id) => cellOf(state.lens, id));
  return state.lens === "against" ? available.slice(0, 2) : available;
}

/** The combinations drawn: those of the lens whose two sources are drawn. */
export function shownCombos(state: WorkspaceState): Combo[] {
  if (state.lens === "against") return [];
  const shown = shownSignals(state);
  return state.combos.filter(
    (c) => c.lens === state.lens && shown.includes(c.a) && shown.includes(c.b)
  );
}

/**
 * The lanes drawn: the state's lanes with only their shown signals, empty ones
 * left out.
 */
function shownLanes(state: WorkspaceState): Lane[] {
  const shown = shownSignals(state);
  return state.lanes
    .map((l) => ({ ...l, signals: l.signals.filter((s) => shown.includes(s)) }))
    .filter((l) => l.signals.length > 0);
}

/** The unit a placed signal takes an axis in under the lens; null for none. */
function axisUnit(state: WorkspaceState, id: SignalId): string | null {
  const cell = cellOf(state.lens, id);
  const placed = state.placed.find((p) => p.id === id);
  if (!cell || !placed) return null;
  return markOf(placed, state.lens, cell)?.noAxis || cell.unit === NO_UNIT ? null : cell.unit;
}

/**
 * The tracks a lane becomes: a track has at most two units, so the signals of
 * a third unit go on a track of their own below.
 */
function laneTracks(state: WorkspaceState, lane: Lane): SignalId[][] {
  const tracks: { units: string[]; signals: SignalId[] }[] = [];
  for (const id of lane.signals) {
    const unit = axisUnit(state, id);
    let track = tracks.find(
      (t) =>
        unit === null ||
        t.units.includes(unit) ||
        t.units.length < UNITS_PER_TRACK
    );
    if (!track) {
      track = { units: [], signals: [] };
      tracks.push(track);
    }
    if (unit !== null && !track.units.includes(unit)) track.units.push(unit);
    track.signals.push(id);
  }
  return tracks.map((t) => t.signals);
}

/**
 * The time-axis grid a smoothed timeline needs, so a window is a whole number
 * of slots at any range.
 */
function timelineGrid(rangeHours: number): number {
  const least = Math.ceil((rangeHours * 60) / MAX_SLOTS);
  return (
    NICE_GRIDS.find((g) => g >= least) ?? NICE_GRIDS[NICE_GRIDS.length - 1]
  );
}

function roundWindow(minutes: number, grid: number): number {
  return Math.ceil(minutes / grid) * grid;
}

function laneHeight(lane: Lane): number {
  if (lane.signals.includes("glucose")) return 2;
  return FAMILY_LANES.find((f) => f.family === lane.family)?.height ?? 1;
}

function bounds(b: Bounds | undefined): Bounds | undefined {
  if (!b || (b.min === undefined && b.max === undefined)) return undefined;
  return {
    ...(b.min !== undefined ? { min: b.min } : {}),
    ...(b.max !== undefined ? { max: b.max } : {}),
  };
}

/** Whether a moment narrows its events at all: a filter or a condition. */
export function isNarrowed(moment: Moment): boolean {
  return !moment.everythingElse && Object.keys(cohortJson(moment)).length > 1;
}

/** A moment as one cohort of the `event_response` query. */
function cohortJson(moment: Moment): Record<string, unknown> {
  if (moment.everythingElse) return { anchor: moment.what, others: true };
  const meal = anchor(moment.what).meal;
  const foods =
    meal && moment.foods && moment.foods.length > 0
      ? moment.foods.map((f) => f.id)
      : undefined;
  const grams = meal ? bounds(moment.grams) : undefined;
  const sinceMeal = bounds(moment.minutesSinceMeal);
  const glucose = bounds(moment.startGlucose);
  const iob = bounds(moment.insulinOnBoard);
  return {
    anchor: moment.what,
    ...(moment.timeOfDay ? { timeOfDay: moment.timeOfDay } : {}),
    ...(moment.weekdays &&
    moment.weekdays.length > 0 &&
    moment.weekdays.length < 7
      ? { dayOfWeek: moment.weekdays }
      : {}),
    ...(grams ? { grams } : {}),
    ...(foods ? { foods } : {}),
    ...(meal && moment.foodCategory?.trim()
      ? { foodCategory: moment.foodCategory.trim() }
      : {}),
    ...(sinceMeal ? { minutesSinceMeal: sinceMeal } : {}),
    ...(glucose ? { startGlucose: glucose } : {}),
    ...(iob ? { insulinOnBoard: iob } : {}),
  };
}

/** Whether a figure or axis applies to every group's anchor, by the catalogue. */
function appliesToAll(figures: EventFigures, id: string, moments: Moment[]): boolean {
  const anchors = figures[id];
  return !!anchors && moments.every((m) => anchors.includes(m.what));
}

/** The figures that apply to every group, by the catalogue's `x-eventFigures`. */
export function outcomesFor(
  moments: Moment[],
  forSpread: boolean,
  figures: EventFigures
): OutcomeChoice[] {
  return OUTCOMES.filter(
    (o) => (forSpread || !o.spreadOnly) && appliesToAll(figures, o.id, moments)
  );
}

/** What each event can lie along, for these groups. */
export function alongsFor(moments: Moment[], figures: EventFigures) {
  return ALONGS.filter((a) => appliesToAll(figures, a.id, moments));
}

/** A chosen figure, or the first that applies when it does not. */
function pickOutcome(
  moments: Moment[],
  chosen: Outcome,
  forSpread: boolean,
  figures: EventFigures
): Outcome {
  const list = outcomesFor(moments, forSpread, figures);
  return list.find((o) => o.id === chosen)?.id ?? list[0]?.id ?? chosen;
}

function pickAlong(moments: Moment[], chosen: Along, figures: EventFigures): Along {
  const list = alongsFor(moments, figures);
  return list.find((a) => a.id === chosen)?.id ?? list[0]?.id ?? chosen;
}

/** The cohorts of the moments, one for each, in order. */
export function eventCohorts(moments: Moment[]): {
  cohorts: Record<string, unknown>[];
  numbers: number[];
} {
  return {
    cohorts: moments.map((m) => cohortJson(m)),
    numbers: moments.map((_, i) => i + 1),
  };
}

/** The leave-out parameters that differ from the API's defaults, and the profile. */
function leaveOutParameters(
  state: WorkspaceState,
  defaults: Record<LeaveOutId, boolean>
): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const l of LEAVE_OUTS)
    if (state.leaveOut[l.id] !== defaults[l.id])
      out[l.parameter] = state.leaveOut[l.id];
  if (state.profileName?.trim()) out.profileName = state.profileName.trim();
  return out;
}

/** The parameters of the `event_response` query: groups, window and options. */
export function eventParameters(
  state: WorkspaceState,
  defaults: Record<LeaveOutId, boolean>
): Record<string, unknown> {
  const rise = state.moments.some(
    (m) => !m.everythingElse && m.what === ANCHOR_RISE
  );
  return {
    cohorts: eventCohorts(state.moments).cohorts,
    minutesBefore: state.event.before,
    minutesAfter: state.event.after,
    ...(state.event.isolatedOnly ? { isolatedOnly: true } : {}),
    ...(rise && state.event.requireCorroboration
      ? { requireCorroboration: true }
      : {}),
    ...(state.event.comparison
      ? { comparisonRange: state.event.comparison }
      : {}),
    ...leaveOutParameters(state, defaults),
  };
}

export interface BuildContext {
  rangeHours: number;
  leaveOut: Record<LeaveOutId, boolean>;
  figures: EventFigures;
  clock?: (minute: number) => string;
}

/** What a day had, as words: "Days with a low". */
export function dayFilterText(
  f: DayFilter,
  clock: (minute: number) => string = clockText
): string {
  switch (f.kind) {
    case "exercise":
      return "Days with exercise";
    case "illness":
      return "Days with illness";
    case "low":
      return "Days with a low";
    case "lowOvernight":
      return `Days with a low from ${clock(0)} to ${clock(360)}`;
    case "carbs": {
      const b = boundsText({ min: f.minCarbs, max: f.maxCarbs }, "g");
      return b ? `Days with ${b} of carbs` : "Days by total carbs";
    }
  }
}

/**
 * The spec of a chart, and which layers each primitive became. Pure, so a
 * change can be tried before it is kept.
 */
export function build(state: WorkspaceState, ctx: BuildContext): Built {
  const signalLayers: Partial<Record<SignalId, string[]>> = {};
  const comboLayers: Record<number, string[]> = {};
  const momentCohorts: CohortRef[][] = state.moments.map(() => []);
  const dayFilterOn =
    state.dayFilter !== null &&
    (state.lens === "typical_day" || state.lens === "daily");
  const dayGroupLayers: string[][] = dayFilterOn
    ? [[], []]
    : state.dayGroups.map(() => []);
  const reads: string[] = [];
  const empty: Built = {
    spec: null,
    panels: [],
    signalLayers,
    comboLayers,
    momentCohorts,
    dayGroupLayers,
    tracks: 0,
    layers: 0,
    reads: [],
    hours: ctx.rangeHours,
  };
  const shown = shownSignals(state);
  const placedOf = (id: SignalId) => state.placed.find((p) => p.id === id)!;
  const read = (dataset: Record<string, unknown>) => {
    const key = JSON.stringify(dataset);
    if (!reads.includes(key)) reads.push(key);
  };
  const own = (id: SignalId, layer: string) =>
    (signalLayers[id] ??= []).push(layer);

  // More groups than the engine has hues: one small chart for each group, each its own panel.
  if (state.lens === "event" && state.moments.length > MAX_OVERLAID_GROUPS) {
    const panels: Record<string, unknown>[] = [];
    let tracks = 0;
    let layers = 0;
    state.moments.forEach((m, g) => {
      const one = build({ ...state, moments: [m] }, ctx);
      const suffix = (id: unknown) => `${String(id)}-g${g + 1}`;
      const rename = (layer: Record<string, unknown>) => ({ ...layer, id: suffix(layer.id) });
      for (const panel of one.panels) {
        const given: unknown = panel.tracks;
        const t = (Array.isArray(given) ? given : []).map((track: Record<string, unknown>) => {
          const layers: unknown = track.layers;
          return { ...track, layers: (Array.isArray(layers) ? layers : []).map(rename) };
        });
        panels.push({
          ...panel,
          id: `chart-${g + 1}`,
          tracks: t,
        });
      }
      for (const sig of SIGNALS) {
        const ids = one.signalLayers[sig.id];
        if (ids) (signalLayers[sig.id] ??= []).push(...ids.map(suffix));
      }
      momentCohorts[g] = one.momentCohorts[0].map((c) => ({ layer: suffix(c.layer), cohort: 1 }));
      for (const r of one.reads) if (!reads.includes(r)) reads.push(r);
      tracks = Math.max(tracks, one.tracks);
      layers = Math.max(layers, one.layers);
    });
    if (panels.length === 0) return empty;
    return {
      spec: {
        version: 1,
        ...(state.title.trim() ? { title: state.title.trim() } : {}),
        range: { hours: ctx.rangeHours },
        panels,
      },
      panels,
      signalLayers,
      comboLayers,
      momentCohorts,
      dayGroupLayers,
      tracks,
      layers,
      reads,
      hours: ctx.rangeHours,
    };
  }

  const report = (panels: Record<string, unknown>[]) => ({
    version: 1,
    ...(state.title.trim() ? { title: state.title.trim() } : {}),
    range: { hours: ctx.rangeHours },
    panels,
  });
  const finish = (
    panels: Record<string, unknown>[],
    tracks: number,
    layers: number
  ): Built => ({
    spec: report(panels),
    panels,
    signalLayers,
    comboLayers,
    momentCohorts,
    dayGroupLayers,
    tracks,
    layers,
    reads,
    hours: ctx.rangeHours,
  });

  const datasetOf = (
    cell: Cell,
    extra: Record<string, unknown> = {}
  ): Record<string, unknown> => ({
    name: cell.dataset,
    ...cell.parameters,
    ...(cell.leaveOut &&
    (state.lens === "typical_day" || state.lens === "event")
      ? leaveOutParameters(state, ctx.leaveOut)
      : {}),
    ...extra,
  });

  if (state.lens === "against") {
    if (shown.length < 2) return empty;
    const [a, b] = shown.map((id) => cellOf("against", id)!);
    const source = (cell: Cell) => ({
      dataset: datasetOf(cell),
      field: cell.field,
    });
    read(datasetOf(a));
    read(datasetOf(b));
    const derived = (op: string) => ({ op, a: source(a), b: source(b) });
    const panels: Record<string, unknown>[] = [
      {
        id: "chart",
        kind: "chart",
        grid: AGAINST_GRID_MINUTES,
        tracks: [
          {
            id: "against",
            layers: [
              { id: "scatter", derived: derived("scatter"), mark: "points" },
            ],
          },
        ],
      },
    ];
    for (const id of shown) own(id, "scatter");
    if (state.findLag) {
      panels.push({
        id: "lag",
        kind: "chart",
        grid: AGAINST_GRID_MINUTES,
        tracks: [
          {
            id: "lag",
            layers: [
              { id: "lag", derived: derived("lag_correlation"), mark: "line" },
            ],
          },
        ],
      });
      for (const id of shown) own(id, "lag");
    }
    return finish(panels, 1, panels.length);
  }

  if (state.lens === "event" && state.event.show !== "curve") {
    const { comparisonRange: _comparison, ...params } = eventParameters(
      state,
      ctx.leaveOut
    );
    const groups = state.moments.length;
    const counts = state.moments.map((_, i) => `events${i + 1}`);
    const layers: Record<string, unknown>[] = [];
    if (state.event.show === "each") {
      const up = pickOutcome(state.moments, state.event.up, false, ctx.figures);
      const along = pickAlong(state.moments, state.event.along, ctx.figures);
      state.moments.forEach((_, i) => {
        const dataset = { name: OUTCOMES_DATASET, ...params, group: i + 1 };
        read(dataset);
        layers.push({
          id: `each-${i + 1}`,
          dataset,
          mark: "points",
          bindings: {
            x: along,
            y: [up],
            ...(state.event.fit && along !== ALONG_TIME_OF_DAY ? { fit: `${up}Fit` } : {}),
          },
        });
        momentCohorts[i].push({ layer: `each-${i + 1}`, cohort: i + 1 });
      });
    } else {
      const spread = state.event.show === "spread";
      const dataset = spread
        ? {
            name: SPREAD_DATASET,
            ...params,
            outcome: pickOutcome(state.moments, state.event.figure, true, ctx.figures),
          }
        : { name: OCCURRENCE_DATASET, ...params };
      read(dataset);
      layers.push({
        id: "events",
        dataset,
        mark: "bars",
        bindings: {
          x: spread ? "range" : state.event.when,
          y: counts.slice(0, groups),
        },
      });
      state.moments.forEach((_, i) =>
        momentCohorts[i].push({ layer: "events", cohort: i + 1 })
      );
    }
    const panel = {
      id: "chart",
      kind: "chart",
      tracks: [{ id: "events", layers }],
    };
    return finish([panel], 1, layers.length);
  }

  const lanes = shownLanes(state);
  if (lanes.length === 0) return empty;
  let grid: number | undefined;
  let layerCount = 0;
  const useGrid = () => {
    if (state.lens === "timeline") grid = timelineGrid(ctx.rangeHours);
  };
  const windowOf = (minutes: number) =>
    state.lens === "timeline" ? roundWindow(minutes, grid!) : minutes;

  const layersFor = (ids: SignalId[]): Record<string, unknown>[] => {
    const layers: Record<string, unknown>[] = [];
    const spans = ids.filter((id) => cellOf(state.lens, id)?.spanKind);
    for (const id of ids) {
      const placed = placedOf(id);
      const cell = cellOf(state.lens, id)!;
      const relate = relateOf(placed, state.lens);
      const mark = markOf(placed, state.lens, cell)!;

      if (cell.spanKind) {
        if (id !== spans[0]) continue;
        const dataset = datasetOf(cell, {
          kinds: spans.map((s) => cellOf(state.lens, s)!.spanKind),
        });
        read(dataset);
        layers.push({
          id: "spans",
          dataset,
          mark: mark.mark,
          bindings: mark.bindings,
        });
        for (const s of spans) own(s, "spans");
        continue;
      }

      const sourceField =
        relate.fromStart && cell.fromStartField
          ? cell.fromStartField
          : cell.field;
      const add = (layerId: string, dataset: Record<string, unknown>) => {
        read(dataset);
        layers.push({
          id: layerId,
          dataset,
          mark: mark.mark,
          bindings: mark.bindings,
        });
        own(id, layerId);
        const source = { dataset, field: sourceField };
        if (relate.sd !== null && cell.smooth.length > 0) {
          useGrid();
          layers.push({
            id: `${layerId}-sd`,
            derived: { op: "band_sd", minutes: windowOf(relate.sd), a: source },
            mark: "band",
          });
          own(id, `${layerId}-sd`);
        }
        if (relate.smooth !== null && cell.smooth.length > 0) {
          useGrid();
          layers.push({
            id: `${layerId}-smooth`,
            derived: {
              op: "rolling_mean",
              minutes: windowOf(relate.smooth),
              a: source,
            },
            mark: "line",
          });
          own(id, `${layerId}-smooth`);
        }
        if (relate.trend && cell.trend) {
          layers.push({
            id: `${layerId}-trend`,
            derived: {
              op: "trend",
              ...(relate.confidence ? { confidence: true } : {}),
              a: source,
            },
            mark: relate.confidence ? "band" : "line",
          });
          own(id, `${layerId}-trend`);
        }
      };

      if (dayFilterOn && cell.happened && state.dayFilter) {
        const filter = state.dayFilter;
        const sets = [filter, { ...filter, others: true }];
        sets.forEach((days, g) => {
          const before = layers.length;
          add(`${id}-${g + 1}`, datasetOf(cell, { days }));
          dayGroupLayers[g].push(...layers.slice(before).map((l) => String(l.id)));
        });
        continue;
      }

      if (state.lens === "typical_day" && cell.days && !dayFilterOn) {
        state.dayGroups.forEach((days, g) => {
          const layerId = state.dayGroups.length > 1 ? `${id}-${g + 1}` : id;
          const dataset = datasetOf(
            cell,
            days.length < 7 ? { dayOfWeek: days } : {}
          );
          const before = layers.length;
          add(layerId, dataset);
          dayGroupLayers[g].push(
            ...layers.slice(before).map((l) => String(l.id))
          );
        });
        continue;
      }

      if (state.lens === "event") {
        add(id, datasetOf(cell, eventParameters(state, ctx.leaveOut)));
        eventCohorts(state.moments).numbers.forEach((cohort, i) =>
          momentCohorts[i].push({ layer: id, cohort })
        );
        continue;
      }

      add(id, datasetOf(cell));
    }
    return layers;
  };

  const tracks: Record<string, unknown>[] = [];
  for (const lane of lanes) {
    laneTracks(state, lane).forEach((signals, i) => {
      const layers = layersFor(signals);
      layerCount += layers.length;
      tracks.push({
        id: i === 0 ? lane.id : `${lane.id}-${i + 1}`,
        height: i === 0 ? laneHeight({ ...lane, signals }) : OVERFLOW_HEIGHT,
        layers,
      });
    });
  }

  for (const combo of shownCombos(state)) {
    const cellA = cellOf(state.lens, combo.a)!;
    const cellB = cellOf(state.lens, combo.b)!;
    const source = (cell: Cell) => {
      const dataset = datasetOf(cell);
      read(dataset);
      return { dataset, field: cell.field };
    };
    useGrid();
    const layerId = `combo-${combo.key}`;
    tracks.push({
      id: layerId,
      height: 1,
      layers: [
        {
          id: layerId,
          derived: { op: combo.op, a: source(cellA), b: source(cellB) },
          mark: combo.mark ?? "line",
        },
      ],
    });
    comboLayers[combo.key] = [layerId];
    layerCount += 1;
  }

  const panel = {
    id: "chart",
    kind: "chart",
    ...(grid !== undefined ? { grid } : {}),
    tracks,
  };
  return finish([panel], tracks.length, layerCount);
}

/** The marks a combination can be drawn as: the op's own. */
export const COMBO_MARKS: Record<CombineOp, string[]> = {
  difference: ["line", "area", "bars", "points"],
  ratio: ["line", "bars", "points"],
};

export function comboLabel(combo: Pick<Combo, "a" | "b" | "op">): string {
  const a = signalLabel(combo.a);
  const b = lowerFirst(signalLabel(combo.b));
  return combo.op === "difference" ? `${a} minus ${b}` : `${a} divided by ${b}`;
}

function weekdayLabel(day: Weekday): string {
  return weekdayName(day);
}

function joinAnd(items: string[]): string {
  if (items.length <= 1) return items.join("");
  return `${items.slice(0, -1).join(", ")} and ${items[items.length - 1]}`;
}

/** Weekdays as words: runs of three or more as "Monday to Friday". */
export function weekdaysText(days: Weekday[]): string {
  const order = [...WEEKDAY_IDS];
  const sorted = order.filter((d) => days.includes(d));
  if (sorted.length === 7) return "every day";
  const runs: Weekday[][] = [];
  for (const day of sorted) {
    const last = runs[runs.length - 1];
    if (last && order.indexOf(day) === order.indexOf(last[last.length - 1]) + 1)
      last.push(day);
    else runs.push([day]);
  }
  return joinAnd(
    runs.flatMap((run) =>
      run.length >= 3
        ? [`${weekdayLabel(run[0])} to ${weekdayLabel(run[run.length - 1])}`]
        : run.map(weekdayLabel)
    )
  );
}

/** A minute of the day, 0 to 1440, as 24-hour clock time; 1440 is "24:00". */
export function clockText(minute: number): string {
  return `${String(Math.floor(minute / 60)).padStart(2, "0")}:${String(minute % 60).padStart(2, "0")}`;
}

/** The end of a window: an end of 0 after a later start is midnight, 1440. */
function endMinute(window: { fromMinute: number; toMinute: number }): number {
  return window.toMinute === 0 && window.fromMinute > 0
    ? 1440
    : window.toMinute;
}

/** Minutes as words: "30 minutes", "1 hour", "5 hours", "3 days". */
export function minutesText(minutes: number): string {
  if (minutes >= 1440 && minutes % 1440 === 0)
    return minutes === 1440 ? "1 day" : `${minutes / 1440} days`;
  if (minutes >= 60 && minutes % 60 === 0)
    return minutes === 60 ? "1 hour" : `${minutes / 60} hours`;
  return `${minutes} minutes`;
}

/** The event window as words: "60 minutes before to 5 hours after". */
export function windowText(before: number, after: number): string {
  return before === 0
    ? `From the event to ${minutesText(after)} after`
    : `${minutesText(before)} before to ${minutesText(after)} after`;
}

/** A moment's narrowings as words; the anchor too when `withAnchor`. */
export function momentText(
  moment: Moment,
  withAnchor: boolean,
  clock: (minute: number) => string,
  units: GlucoseUnits
): string {
  if (moment.everythingElse)
    return withAnchor
      ? `all other ${anchorPhrase(moment.what)}`
      : "everything else";
  const parts: string[] = [];
  if (moment.timeOfDay)
    parts.push(
      `${clock(moment.timeOfDay.fromMinute)} to ${clock(endMinute(moment.timeOfDay))}`
    );
  if (
    moment.weekdays &&
    moment.weekdays.length > 0 &&
    moment.weekdays.length < 7
  )
    parts.push(`on ${weekdaysText(moment.weekdays)}`);
  if (anchor(moment.what).meal) {
    const g = moment.grams;
    if (g?.min !== undefined && g.max !== undefined)
      parts.push(`${g.min} to ${g.max} g`);
    else if (g?.min !== undefined) parts.push(`at least ${g.min} g`);
    else if (g?.max !== undefined) parts.push(`at most ${g.max} g`);
    if (moment.foods)
      parts.push(
        moment.foods.length === 0
          ? "with a food"
          : `with ${moment.foods.map((f) => f.name).join(" or ")}`
      );
    if (moment.foodCategory?.trim())
      parts.push(`with ${moment.foodCategory.trim()}`);
  }
  const since = sinceMealText(moment.minutesSinceMeal);
  if (since) parts.push(since);
  const glucose = boundsText(
    toDisplay(moment.startGlucose, units),
    unitLabel(units)
  );
  if (glucose) parts.push(`glucose ${glucose} at the event`);
  const iob = boundsText(moment.insulinOnBoard, "U");
  if (iob) parts.push(`insulin on board ${iob} at the event`);
  const phrase = anchorPhrase(moment.what);
  if (parts.length === 0) return withAnchor ? `all ${phrase}` : "all";
  return withAnchor ? `${phrase} ${parts.join(", ")}` : parts.join(", ");
}

/** The label of a glucose unit, as the API writes it. */
export function unitLabel(units: GlucoseUnits): string {
  return units === "mmol" ? "mmol/L" : "mg/dL";
}

/** Glucose bounds in mg/dL, in the given display unit. */
export function toDisplay(
  b: Bounds | undefined,
  units: GlucoseUnits
): Bounds | undefined {
  if (!b) return undefined;
  return {
    ...(b.min !== undefined ? { min: convertToDisplayUnits(b.min, units) } : {}),
    ...(b.max !== undefined ? { max: convertToDisplayUnits(b.max, units) } : {}),
  };
}

/** Glucose bounds in a display unit, as whole mg/dL. */
export function fromDisplay(b: Bounds, units: GlucoseUnits): Bounds {
  return {
    ...(b.min !== undefined
      ? { min: convertFromDisplayUnits(b.min, units) }
      : {}),
    ...(b.max !== undefined
      ? { max: convertFromDisplayUnits(b.max, units) }
      : {}),
  };
}

/** A range as words: "70 to 180 mg/dL", "at least 2 U". */
export function boundsText(
  b: Bounds | undefined,
  unit: string
): string | null {
  if (b?.min !== undefined && b.max !== undefined)
    return `${b.min} to ${b.max} ${unit}`;
  if (b?.min !== undefined) return `at least ${b.min} ${unit}`;
  if (b?.max !== undefined) return `at most ${b.max} ${unit}`;
  return null;
}

function beforeText(minutes: number): string {
  return minutes === 60 ? "hour" : minutesText(minutes);
}

/** Time since the last carbs as words: "no carbs in the 4 hours before". */
export function sinceMealText(b: Bounds | undefined): string | null {
  if (!b) return null;
  if (b.min !== undefined && b.max === undefined)
    return `no carbs in the ${beforeText(b.min)} before`;
  if (b.max !== undefined && b.min === undefined)
    return `carbs in the ${beforeText(b.max)} before`;
  if (b.min !== undefined && b.max !== undefined)
    return `the last carbs ${minutesText(b.min)} to ${minutesText(b.max)} before`;
  return null;
}

function sentenceCase(labels: string[]): string {
  return joinAnd(labels.map((l, i) => (i === 0 ? l : lowerFirst(l))));
}

export function rangeText(hours: number): string {
  const preset = RANGE_PRESETS.find((p) => p.hours === hours);
  if (preset) return `over the last ${rangeLabel(preset.id)}`;
  return hours % 24 === 0
    ? `over the last ${hours / 24} days`
    : `over the last ${hours} hours`;
}

function daysText(hours: number): string {
  const days = Math.floor(hours / 24);
  return days === 1 ? "1 day" : `${days} days`;
}

/**
 * A chart as the member builds it: signals placed in lanes, a lens that folds
 * time, a range, and the lens's moments, day groups and options. It writes an
 * ordinary spec; the API's validator stays the judge of it. Every operation
 * has a reason it cannot be done now, read before the control is used; an
 * operation that is done anyway returns that reason and changes nothing.
 */
export class Workspace {
  state = $state<WorkspaceState>(emptyState(FALLBACK_LEAVE_OUT));

  /**
   * How the caption writes a minute of the day; the UI sets the member's clock
   * format.
   */
  clock: (minute: number) => string = clockText;

  /** How the caption writes two dates; the UI sets the app's date format. */
  dates: (span: DateSpan) => string = (span) => `${span.from} to ${span.to}`;

  readonly #catalogue: CustomReportCatalogue;
  readonly #host: WorkspaceHost;
  readonly #leaveOut: Record<LeaveOutId, boolean>;
  readonly #figures: EventFigures;

  readonly built: Built = $derived(build(this.state, this.#context(this.state)));

  constructor(
    catalogue: CustomReportCatalogue,
    starter?: Starter,
    host: WorkspaceHost = STANDALONE
  ) {
    this.#catalogue = catalogue;
    this.#host = host;
    this.#leaveOut = leaveOutDefaults(catalogue);
    this.#figures = eventFiguresOf(catalogue);
    this.state = emptyState(this.#leaveOut);
    if (starter) this.load(starter);
  }

  get catalogue(): CustomReportCatalogue {
    return this.#catalogue;
  }

  get leaveOutDefaults(): Record<LeaveOutId, boolean> {
    return this.#leaveOut;
  }

  #context(state: WorkspaceState): BuildContext {
    return {
      rangeHours: this.#hoursOf(state),
      leaveOut: this.#leaveOut,
      figures: this.#figures,
      clock: this.clock,
    };
  }

  #hoursOf(state: WorkspaceState): number {
    return state.range ?? this.#host.reportHours();
  }

  get title(): string {
    return this.state.title;
  }

  set title(value: string) {
    this.state.title = value.slice(0, this.#catalogue.maxTitleLength);
  }

  get lens(): LensId {
    return this.state.lens;
  }

  /** The range the chart draws, its own or the report's. */
  get rangeHours(): number {
    return this.#hoursOf(this.state);
  }

  /** The chart's own range, or null when it takes the report's. */
  get ownRange(): number | null {
    return this.state.range;
  }

  get moments(): Moment[] {
    return this.state.moments;
  }

  get dayGroups(): Weekday[][] {
    return this.state.dayGroups;
  }

  get findLag(): boolean {
    return this.state.findLag;
  }

  get event(): EventOptions {
    return this.state.event;
  }

  get leaveOut(): Record<LeaveOutId, boolean> {
    return this.state.leaveOut;
  }

  get profileName(): string | null {
    return this.state.profileName;
  }

  get maxMoments(): number {
    const max: unknown = this.#catalogue.datasets.find(
      (d) => d.name === EVENT_DATASET
    )?.parameters?.properties?.cohorts?.maxItems;
    return typeof max === "number" ? max : MAX_MOMENTS_FALLBACK;
  }

  get beforeChoices(): number[] {
    return choicesOf(this.#catalogue, "minutesBefore", EVENT_BEFORE_CHOICES);
  }

  get afterChoices(): number[] {
    return choicesOf(this.#catalogue, "minutesAfter", EVENT_AFTER_CHOICES);
  }

  /** The signals drawn, in lane order. */
  get shown(): SignalId[] {
    return this.lanes.flatMap((l) => l.signals);
  }

  get combos(): Combo[] {
    return shownCombos(this.state);
  }

  /**
   * The lanes drawn. Under `against` there are no lanes; the two signals are
   * `shown` in order: along, then up.
   */
  get lanes(): Lane[] {
    return this.state.lens === "against" ? [] : shownLanes(this.state);
  }

  /** The signals under `against`: the first along the axis, the second up. */
  get againstPair(): SignalId[] {
    return this.state.lens === "against" ? shownSignals(this.state) : [];
  }

  /** What a chart around an event shows; always the curve under other lenses. */
  get show(): EventShow {
    return this.state.lens === "event" ? this.state.event.show : "curve";
  }

  /** Whether the chart shows the events one by one rather than signals. */
  get eventList(): boolean {
    return this.show !== "curve";
  }

  /** Why a "Show" choice cannot be used now, or null. */
  showReason(show: EventShow): Reason {
    if (show === "curve" || show === this.state.event.show) return null;
    const dataset = listDataset(show);
    const found = this.#catalogue.datasets.find((d) => d.name === dataset);
    if (!found) return "This report engine cannot show this.";
    if (!found.permitted || found.fields.some((f) => f.scope && !f.permitted))
      return "Your access cannot read treatments, which this needs.";
    return this.#reason((d) => {
      d.event.show = show;
      d.event.comparison = null;
    });
  }

  /**
   * Set when a change of view removed the earlier period, which only the curve over time draws; the
   * view says so until the member changes the period or goes back to the curve. Not saved.
   */
  comparisonDropped = $state(false);

  setShow(show: EventShow): Reason {
    const reason = this.showReason(show);
    if (reason) return reason;
    const had = this.state.event.comparison !== null;
    const done = this.#commit((d) => {
      d.event.show = show;
      if (show !== "curve") d.event.comparison = null;
    });
    if (!done) this.comparisonDropped = show !== "curve" && (had || this.comparisonDropped);
    return done;
  }

  /** Why an earlier period cannot be added now, or null: only the curve over time draws one. */
  get comparisonReason(): Reason {
    return this.eventList ? "Each event, Spread and When show the events of the range only." : null;
  }

  /** Each event: the figures up, and what they lie along, that apply to the groups. */
  get upChoices() {
    return outcomesFor(this.state.moments, false, this.#figures);
  }

  get alongChoices() {
    return alongsFor(this.state.moments, this.#figures);
  }

  get figureChoices() {
    return outcomesFor(this.state.moments, true, this.#figures);
  }

  /** The figure up as drawn: the chosen one, or the first that applies. */
  get up(): Outcome {
    return pickOutcome(this.state.moments, this.state.event.up, false, this.#figures);
  }

  get along(): Along {
    return pickAlong(this.state.moments, this.state.event.along, this.#figures);
  }

  /** Whether a line can go through the points: not along the time of day, which wraps at midnight. */
  get fitApplies(): boolean {
    return this.along !== ALONG_TIME_OF_DAY;
  }

  get figure(): Outcome {
    return pickOutcome(this.state.moments, this.state.event.figure, true, this.#figures);
  }

  /** Whether the chart draws one small chart for each group: more groups than hues. */
  get smallMultiples(): boolean {
    return this.state.lens === "event" && this.state.moments.length > MAX_OVERLAID_GROUPS;
  }

  /** Why the reader cannot use an anchor, or null: sleep starts and wake-ups need the sleep permission. */
  anchorReason(id: AnchorId): Reason {
    if (!SLEEP_ANCHORS.includes(id)) return null;
    return this.#datasetPermitted("sleep") ? null : "Your access cannot read sleep, which this needs.";
  }

  /** The engine's one-line note on an anchor ("a site, cannula or pod change"), or null. */
  anchorNote(id: AnchorId): string | null {
    return this.#anchorNotes()[id]?.description ?? null;
  }

  /** The event datasets' notes on each kind of event: the catalogue's `x-eventAnchors`. */
  #anchorNotes(): Partial<Record<string, { description: string | null; rule: string | null }>> {
    const params: unknown = this.#catalogue.datasets.find((d) => d.name === EVENT_DATASET)?.parameters;
    const notes = objectAt(params, "x-eventAnchors") ?? {};
    const text = (v: unknown) => (typeof v === "string" && v.trim() ? v : null);
    return Object.fromEntries(
      Object.entries(notes).map(([id, note]) => [
        id,
        { description: text(objectAt({ note }, "note")?.description), rule: text(objectAt({ note }, "note")?.rule) },
      ])
    );
  }

  #datasetPermitted(name: string): boolean {
    return this.#catalogue.datasets.find((d) => d.name === name)?.permitted ?? false;
  }

  /** Signals the lens draws whose data the reader's access cannot read. */
  get noAccess(): SignalId[] {
    return SIGNALS.filter((s) => {
      const cell = cellOf(this.state.lens, s.id);
      return cell && !this.#datasetPermitted(cell.dataset);
    }).map((s) => s.id);
  }

  /** The chart's options in a few words, for the closed "Options" row. */
  get optionsSummary(): string {
    const s = this.state;
    const parts: string[] = [];
    if (s.lens === "event") parts.push(lowerFirst(windowText(s.event.before, s.event.after)));
    if (this.leaveOutApplies) {
      const left = LEAVE_OUTS.filter((l) => s.leaveOut[l.id]).map((l) => lowerFirst(leaveOutLabel(l.id)));
      parts.push(left.length > 0 ? `without ${joinAnd(left)}` : "all time");
    }
    return parts.join(", ");
  }

  /** Whether the lens groups days: Typical day and Day by day. */
  get groupsDays(): boolean {
    return this.state.lens === "typical_day" || this.state.lens === "daily";
  }

  get dayFilter(): DayFilter | null {
    return this.state.dayFilter;
  }

  /** Why a day condition cannot be used now, or null: exercise, illness and carbs read treatments. */
  dayKindReason(kind: DayKind): Reason {
    const needs = DAY_KINDS.find((k) => k.id === kind)?.treatments;
    if (needs && !this.#datasetPermitted("treatments"))
      return "Your access cannot read treatments, which this needs.";
    return this.#reason((d) => {
      d.dayFilter = { kind };
    });
  }

  setDayFilter(filter: DayFilter | null): Reason {
    if (filter) {
      const reason = this.dayKindReason(filter.kind);
      if (reason && this.state.dayFilter?.kind !== filter.kind) return reason;
    }
    return this.#commit((d) => {
      d.dayFilter = filter ? { ...filter } : null;
    });
  }

  /** The two groups of days a day condition makes, with their words. */
  get dayFilterGroups(): string[] {
    const f = this.state.dayFilter;
    return f ? [dayFilterText(f, this.clock), "All other days"] : [];
  }

  /** Whether the chart draws nothing yet. */
  get empty(): boolean {
    if (this.eventList) return false;
    return shownSignals(this.state).length === 0;
  }

  /** What the member does next before the chart can run, or null. */
  get incomplete(): string | null {
    if (this.eventList) return null;
    const shown = shownSignals(this.state);
    if (this.state.lens === "against" && shown.length === 1)
      return "Select one more signal.";
    if (shown.length === 0) return "Select a signal.";
    return null;
  }

  /** Whether the lens draws a signal whose dataset can leave time out. */
  get leaveOutApplies(): boolean {
    if (this.state.lens !== "typical_day" && this.state.lens !== "event")
      return false;
    if (this.eventList) return true;
    return shownSignals(this.state).some(
      (id) => cellOf(this.state.lens, id)?.leaveOut
    );
  }

  /** Shown signals that the leave-out options do not narrow. */
  get leaveOutMisses(): SignalId[] {
    if (!this.leaveOutApplies || this.eventList) return [];
    return shownSignals(this.state).filter(
      (id) => !cellOf(this.state.lens, id)?.leaveOut
    );
  }

  /** Whether a group of events is rises with no carbs. */
  get hasRises(): boolean {
    return this.state.moments.some(
      (m) => !m.everythingElse && m.what === ANCHOR_RISE
    );
  }

  /** Things the spec leaves out or approximates, as notes for the member. */
  get notes(): string[] {
    const notes: string[] = [];
    if (this.state.lens !== "event") return notes;
    for (const m of this.state.moments) {
      if (!m.everythingElse && m.foods && m.foods.length === 0)
        notes.push("Pick a food. Until then, the group shows all meals.");
    }
    return notes;
  }

  /**
   * The moments with their words, whether the engine only approximates them,
   * and where their events land.
   */
  get momentViews(): (Moment & {
    text: string;
    cohorts: CohortRef[];
  })[] {
    const sameAnchor =
      distinct(this.state.moments.map((m) => m.what)).length === 1;
    return this.state.moments.map((m, i) => ({
      ...m,
      text: momentText(m, !sameAnchor, this.clock, this.glucoseUnits),
      cohorts: this.cohortsOf(i),
    }));
  }

  /**
   * A group's name in the member's words, with its letter: "A: Meals 06:00 to 08:30". Heads a small
   * chart of the group; the engine's own name for it stays out of view.
   */
  groupHeading(index: number): string {
    const m = this.state.moments[index];
    if (!m) return "";
    return `${GROUP_LETTERS[index]}: ${upperFirst(momentText(m, true, this.clock, this.glucoseUnits))}`;
  }

  /** The unit the tenant's reports show glucose in: the catalogue's glucose fields carry it. */
  get glucoseUnits(): GlucoseUnits {
    const unit = this.#catalogue.datasets
      .find((d) => d.name === EVENT_DATASET)
      ?.fields.find((f) => f.name === "glucoseP50")?.unit;
    return unit === "mmol/L" ? "mmol" : "mg/dl";
  }

  get glucoseUnit(): string {
    return unitLabel(this.glucoseUnits);
  }

  /**
   * Whether the chart draws more than one group of events: more than one group,
   * or an earlier period beside them. Then each group is drawn in its own hue;
   * one group alone keeps each signal's colour.
   */
  get groupsApart(): boolean {
    if (this.state.lens !== "event" || this.smallMultiples) return false;
    return this.state.moments.length > 1 || this.state.event.comparison !== null;
  }

  /** The schema of one property of an event group, or undefined. */
  #cohortProperty(name: string): Record<string, unknown> | undefined {
    const cohorts: unknown = this.#catalogue.datasets.find(
      (d) => d.name === EVENT_DATASET
    )?.parameters?.properties?.cohorts;
    const items = objectAt(cohorts, "items");
    return objectAt(objectAt(items, "properties"), name);
  }

  /** The lowest and highest value a condition takes, from the schema. */
  conditionLimits(kind: ConditionKind): { min: number; max: number } {
    const props = objectAt(this.#cohortProperty(kind), "properties");
    const min = objectAt(props, "min")?.minimum;
    const max = objectAt(props, "max")?.maximum;
    return {
      min: typeof min === "number" ? min : 0,
      max: typeof max === "number" ? max : 1440,
    };
  }

  /** Whether the engine knows the condition at all. */
  hasCondition(kind: ConditionKind): boolean {
    return this.#cohortProperty(kind) !== undefined;
  }

  /** Why the reader cannot use a condition now, or null. */
  conditionReason(kind: ConditionKind): Reason {
    const scope = this.#cohortProperty(kind)?.["x-scope"];
    if (typeof scope !== "string") return null;
    const holder = this.#catalogue.datasets.find(
      (d) => d.requiredScope === scope
    );
    const held =
      holder?.permitted ??
      this.#catalogue.datasets.some((d) =>
        d.fields.some((f) => f.scope === scope && f.permitted)
      );
    return held
      ? null
      : "Your access cannot read treatments, which this needs.";
  }

  /** The engine's rule for when an event of the kind starts and ends (lows, highs, carbs in a low), or null. */
  anchorRule(what: AnchorId): string | null {
    return this.#anchorNotes()[what]?.rule ?? null;
  }

  /** The signals the lens draws, each with the reason it cannot be placed now. */
  get signals(): SignalOption[] {
    const blocked = this.noAccess;
    return SIGNALS.filter(
      (s) => cellOf(this.state.lens, s.id) && !blocked.includes(s.id)
    ).map((s) => {
      const placed = this.state.placed.some((p) => p.id === s.id);
      return {
        id: s.id,
        placed,
        reason: placed ? null : this.#reason((d) => this.#place(d, s.id)),
      };
    });
  }

  /** Signals only other lenses draw: one option for each lens that draws one. */
  get elsewhere(): ElsewhereOption[] {
    return SIGNALS.filter((s) => !cellOf(this.state.lens, s.id)).flatMap((s) =>
      lensesOf(s.id)
        .filter((lens) => this.#datasetPermitted(cellOf(lens, s.id)!.dataset))
        .map((lens) => {
        const placed = this.state.placed.some((p) => p.id === s.id);
        return {
          id: s.id,
          lens,
          placed,
          reason: this.#reason((d) => {
            this.#switchLens(d, lens);
            return placed ? null : this.#place(d, s.id);
          }),
        };
      })
    );
  }

  /** The spec layer ids a placed signal produced. */
  layersOf(id: SignalId): string[] {
    return this.built.signalLayers[id] ?? [];
  }

  comboLayersOf(key: number): string[] {
    return this.built.comboLayers[key] ?? [];
  }

  /** Where each of a moment's events land: one cohort in each signal's layer. */
  cohortsOf(index: number): CohortRef[] {
    return this.built.momentCohorts[index] ?? [];
  }

  dayGroupLayersOf(index: number): string[] {
    return this.built.dayGroupLayers[index] ?? [];
  }

  /** The spec to run, or null while `incomplete`. */
  toSpec(): Record<string, unknown> | null {
    return this.built.spec;
  }

  /**
   * Applies `change` to a copy and returns why the copy cannot be kept: the
   * change's own refusal, or a limit of the chart or the report.
   */
  #attempt(change: (draft: WorkspaceState) => Reason | void): {
    draft: WorkspaceState;
    reason: Reason;
  } {
    // A server build's snapshot returns the state itself, so the draft is cloned again.
    const draft: WorkspaceState = structuredClone($state.snapshot(this.state));
    const refused = change(draft);
    return { draft, reason: refused || this.#overLimit(draft) };
  }

  #reason(change: (draft: WorkspaceState) => Reason | void): Reason {
    return this.#attempt(change).reason;
  }

  #commit(change: (draft: WorkspaceState) => Reason | void): Reason {
    const { draft, reason } = this.#attempt(change);
    if (!reason) this.state = draft;
    return reason;
  }

  #overLimit(state: WorkspaceState): Reason {
    const built = build(state, this.#context(state));
    const c = this.#catalogue;
    const range = this.#rangeReason(state, this.#hoursOf(state));
    if (range) return range;
    if (built.tracks > c.maxTracks)
      return `A chart has at most ${c.maxTracks} lanes. Remove a signal first.`;
    if (built.layers > c.maxLayers)
      return `A chart draws at most ${c.maxLayers} layers. Remove a signal or an option first.`;
    if (built.reads.length > c.maxReads)
      return `A report reads at most ${c.maxReads} sets of data.`;
    if (state.moments.length > this.maxMoments)
      return `A chart has at most ${this.maxMoments} groups of events.`;
    if (state.dayGroups.length > MAX_DAY_GROUPS)
      return `A chart has at most ${MAX_DAY_GROUPS} groups of days.`;
    return this.#host.budget(built);
  }

  /**
   * The longest range the shown signals' data covers, and the signal that
   * limits it.
   */
  #rangeLimit(state: WorkspaceState): { hours: number; by: SignalId | null } {
    let limit: { hours: number; by: SignalId | null } = {
      hours: this.#catalogue.maxRangeHours,
      by: null,
    };
    // A view of the events one by one draws no signal: only its own dataset limits the range.
    if (state.lens === "event" && state.event.show !== "curve") {
      const hours = this.#catalogue.datasets.find((d) => d.name === listDataset(state.event.show))?.maxRangeHours;
      return hours !== undefined && hours < limit.hours ? { hours, by: null } : limit;
    }
    for (const id of shownSignals(state)) {
      const hours = this.#catalogue.datasets.find(
        (d) => d.name === cellOf(state.lens, id)?.dataset
      )?.maxRangeHours;
      if (hours !== undefined && hours < limit.hours) limit = { hours, by: id };
    }
    return limit;
  }

  #rangeReason(state: WorkspaceState, hours: number): Reason {
    const limit = this.#rangeLimit(state);
    if (hours <= limit.hours) return null;
    return limit.by
      ? `${signalLabel(limit.by)} shows at most ${daysText(limit.hours)}. Choose a shorter range first.`
      : `A report covers at most ${daysText(limit.hours)}.`;
  }

  /** The presets of the chart's own range, each with why it cannot be chosen. */
  get ranges(): RangeOption[] {
    return RANGE_PRESETS.map((p) => ({
      ...p,
      label: rangeLabel(p.id),
      reason: this.#reason((d) => {
        d.range = p.hours;
      }),
    }));
  }

  /** Why the chart cannot take the report's range of `hours`, or null. */
  reportRangeReason(hours: number): Reason {
    if (this.state.range !== null) return null;
    return this.#rangeReason(this.state, hours);
  }

  /** Sets the chart's own range; null takes the report's. */
  setRange(hours: number | null): Reason {
    return this.#commit((d) => {
      d.range = hours;
    });
  }

  /** Why the chart cannot take the report's range now, or null. */
  get sameAsReportReason(): Reason {
    return this.#reason((d) => {
      d.range = null;
    });
  }

  #place(d: WorkspaceState, id: SignalId): Reason {
    if (d.placed.some((p) => p.id === id)) return null;
    if (!cellOf(d.lens, id))
      return `${lensLabel(d.lens)} cannot show this signal.`;
    if (d.lens === "against" && shownSignals(d).length >= 2)
      return "This view shows two signals. Remove one first.";
    d.placed.push({ id, marks: {}, relate: {} });
    placeInFamilyLane(d, id);
    return null;
  }

  /** Places the signal in its family's lane, or removes it. */
  toggleSignal(id: SignalId): Reason {
    return this.#commit((d) => {
      const at = d.placed.findIndex((p) => p.id === id);
      if (at < 0) return this.#place(d, id);
      d.placed.splice(at, 1);
      for (const lane of d.lanes)
        lane.signals = lane.signals.filter((s) => s !== id);
      d.lanes = d.lanes.filter((l) => l.signals.length > 0);
      d.combos = d.combos.filter((c) => c.a !== id && c.b !== id);
    });
  }

  /** Switches to `lens` and places the signal there. */
  placeIn(id: SignalId, lens: LensId): Reason {
    return this.#commit((d) => {
      this.#switchLens(d, lens);
      return this.#place(d, id);
    });
  }

  /** Whether the signal can have a lane of its own (spans share one layer). */
  canOwnLane(id: SignalId): boolean {
    return (
      this.state.lens !== "against" && !cellOf(this.state.lens, id)?.spanKind
    );
  }

  ownLane(id: SignalId): boolean {
    return this.state.lanes.some((l) => l.own && l.signals.includes(id));
  }

  /** Why the signal cannot move to a lane of its own (or back) now, or null. */
  ownLaneReason(id: SignalId, on: boolean): Reason {
    return this.#reason((d) => this.#setOwnLane(d, id, on));
  }

  setOwnLane(id: SignalId, on: boolean): Reason {
    return this.#commit((d) => this.#setOwnLane(d, id, on));
  }

  #setOwnLane(d: WorkspaceState, id: SignalId, on: boolean): Reason {
    const from = d.lanes.findIndex((l) => l.signals.includes(id));
    if (from < 0) return "Place the signal first.";
    if (d.lanes[from].own === on) return null;
    if (on) {
      d.lanes[from].signals = d.lanes[from].signals.filter((s) => s !== id);
      d.lanes.splice(from + 1, 0, {
        id: `own-${id}`,
        family: signalOf(id).family,
        own: true,
        signals: [id],
      });
      d.lanes = d.lanes.filter((l) => l.signals.length > 0);
    } else {
      d.lanes.splice(from, 1);
      placeInFamilyLane(d, id);
    }
    return null;
  }

  /**
   * The marks the signal can be drawn as under the lens; more than one means
   * the member can choose.
   */
  marksFor(
    id: SignalId
  ): { mark: string; label: string; chosen: boolean; reason: Reason }[] {
    const placed = this.state.placed.find((p) => p.id === id);
    const cell = cellOf(this.state.lens, id);
    if (!cell || !placed) return [];
    const chosen = markOf(placed, this.state.lens, cell)?.mark;
    return markChoices(placed, this.state.lens, cell).map((m) => ({
      mark: m.mark,
      label: markLabel(m.word),
      chosen: m.mark === chosen,
      reason:
        m.mark === chosen
          ? null
          : this.#reason((d) => this.#setMark(d, id, m.mark)),
    }));
  }

  #setMark(d: WorkspaceState, id: SignalId, mark: string): Reason {
    const placed = d.placed.find((p) => p.id === id);
    const cell = cellOf(d.lens, id);
    if (!placed || !cell) return "Place the signal first.";
    if (!markChoices(placed, d.lens, cell).some((m) => m.mark === mark))
      return "This signal cannot be drawn that way here.";
    placed.marks[d.lens] = mark;
    return null;
  }

  setMark(id: SignalId, mark: string): Reason {
    return this.#commit((d) => this.#setMark(d, id, mark));
  }

  /** The relate options that apply to the signal under the lens. */
  relateOptions(id: SignalId): {
    smooth: number[];
    trend: boolean;
    fromStart: boolean;
  } {
    const cell = cellOf(this.state.lens, id);
    return {
      smooth: cell?.smooth ?? [],
      trend: cell?.trend ?? false,
      fromStart: cell?.fromStartMarks !== undefined,
    };
  }

  relate(id: SignalId): Relate {
    const placed = this.state.placed.find((p) => p.id === id);
    return placed ? relateOf(placed, this.state.lens) : { ...NO_RELATE };
  }

  #setRelate(d: WorkspaceState, id: SignalId, patch: Partial<Relate>): Reason {
    const options = this.relateOptions(id);
    if (patch.smooth != null && !options.smooth.includes(patch.smooth))
      return "This signal cannot be smoothed so here.";
    if (patch.sd != null && !options.smooth.includes(patch.sd))
      return "This signal has no mean and standard deviation here.";
    if (patch.trend && !options.trend) return "This signal has no trend here.";
    if (patch.fromStart && !options.fromStart)
      return "Only glucose around an event can start from zero.";
    const placed = d.placed.find((p) => p.id === id);
    if (!placed) return "Place the signal first.";
    const next = { ...relateOf(placed, d.lens), ...patch };
    if (!next.trend) next.confidence = false;
    placed.relate[d.lens] = next;
    return null;
  }

  /** Why the relate change cannot be made now, or null. */
  relateReason(id: SignalId, patch: Partial<Relate>): Reason {
    return this.#reason((d) => this.#setRelate(d, id, patch));
  }

  setRelate(id: SignalId, patch: Partial<Relate>): Reason {
    return this.#commit((d) => this.#setRelate(d, id, patch));
  }

  /** The placed signals `id` can be combined with, under the lens. */
  partnersOf(id: SignalId): SignalId[] {
    if (this.state.lens === "against" || this.state.lens === "event") return [];
    if (!cellOf(this.state.lens, id)?.combine) return [];
    return shownSignals(this.state).filter(
      (other) => other !== id && cellOf(this.state.lens, other)?.combine
    );
  }

  /** The ops that can combine `a` with `b` at all: subtract needs one unit. */
  opsFor(a: SignalId, b: SignalId): CombineOp[] {
    const unitA = cellOf(this.state.lens, a)?.unit;
    const unitB = cellOf(this.state.lens, b)?.unit;
    return unitA === unitB ? ["difference", "ratio"] : ["ratio"];
  }

  #combine(d: WorkspaceState, a: SignalId, b: SignalId, op: CombineOp): Reason {
    const cellA = cellOf(d.lens, a);
    const cellB = cellOf(d.lens, b);
    if (!cellA?.combine || !cellB?.combine)
      return "These signals cannot be combined here.";
    if (op === "difference" && cellA.unit !== cellB.unit)
      return "Subtract takes two signals in one unit.";
    if (d.combos.some((c) => c.lens === d.lens && c.a === a && c.b === b && c.op === op))
      return "The chart already shows this.";
    d.combos.push({ key: freshKey(), lens: d.lens, a, b, op });
    return null;
  }

  combineReason(a: SignalId, b: SignalId, op: CombineOp): Reason {
    return this.#reason((d) => this.#combine(d, a, b, op));
  }

  combine(a: SignalId, b: SignalId, op: CombineOp): Reason {
    return this.#commit((d) => this.#combine(d, a, b, op));
  }

  removeCombo(key: number): Reason {
    return this.#commit((d) => {
      d.combos = d.combos.filter((c) => c.key !== key);
    });
  }

  comboMarkReason(key: number, mark: string): Reason {
    return this.#reason((d) => this.#setComboMark(d, key, mark));
  }

  setComboMark(key: number, mark: string): Reason {
    return this.#commit((d) => this.#setComboMark(d, key, mark));
  }

  #setComboMark(d: WorkspaceState, key: number, mark: string): Reason {
    const combo = d.combos.find((c) => c.key === key);
    if (!combo) return "No such line.";
    if (!COMBO_MARKS[combo.op].includes(mark))
      return "This line cannot be drawn that way.";
    combo.mark = mark;
    return null;
  }

  get findLagReason(): Reason {
    return this.#reason((d) => {
      d.findLag = !d.findLag;
    });
  }

  setFindLag(on: boolean): Reason {
    return this.#commit((d) => {
      d.findLag = on;
    });
  }

  /** Swaps the two signals of `against`, so the other one lies along the axis. */
  swapAgainst(): Reason {
    return this.#commit((d) => {
      const pair = shownSignals(d);
      if (pair.length < 2) return "Select two signals.";
      const a = d.placed.findIndex((p) => p.id === pair[0]);
      const b = d.placed.findIndex((p) => p.id === pair[1]);
      [d.placed[a], d.placed[b]] = [d.placed[b], d.placed[a]];
    });
  }

  /**
   * Switches the draft's lens. A range of the chart's own that the lens's data
   * cannot cover becomes the longest preset it can; a chart on the report's
   * range keeps it, and the range presets say why it does not fit.
   */
  #switchLens(d: WorkspaceState, id: LensId) {
    d.lens = id;
    if (d.range !== null && this.#rangeReason(d, d.range)) {
      const fits = RANGE_PRESETS.filter((p) => !this.#rangeReason(d, p.hours));
      d.range = fits[fits.length - 1]?.hours ?? RANGE_PRESETS[0].hours;
    }
  }

  /**
   * Another lens. Placed signals the lens cannot draw stay placed but hidden,
   * so a switch back shows them again.
   */
  setLens(id: LensId): Reason {
    return this.#commit((d) => this.#toLens(d, id));
  }

  /** Why the chart cannot be shown through `id` now, or null: the answer the lens switch shows before it is used. */
  lensReason(id: LensId): Reason {
    if (id === this.state.lens) return null;
    return this.#reason((d) => this.#toLens(d, id));
  }

  #toLens(d: WorkspaceState, id: LensId) {
    this.#switchLens(d, id);
    if (d.range === null && this.#rangeReason(d, this.#hoursOf(d))) {
      const fits = RANGE_PRESETS.filter((p) => !this.#rangeReason(d, p.hours));
      d.range = fits[fits.length - 1]?.hours ?? RANGE_PRESETS[0].hours;
    }
  }

  get addMomentReason(): Reason {
    return this.#reason((d) => this.#addMoment(d, d.moments[0]?.what));
  }

  #addMoment(d: WorkspaceState, what: AnchorId = ANCHOR_MEAL): Reason {
    if (d.moments.length >= this.maxMoments)
      return `A chart has at most ${this.maxMoments} groups of events.`;
    if (d.moments.some((m) => m.everythingElse) && d.moments.length >= MAX_OVERLAID_GROUPS)
      return "Remove everything else first. It can go with at most two other groups.";
    const at = d.moments.findIndex((m) => m.everythingElse);
    d.moments.splice(at < 0 ? d.moments.length : at, 0, {
      key: freshKey(),
      what,
    });
    return null;
  }

  addMoment(what: AnchorId = ANCHOR_MEAL): Reason {
    return this.#commit((d) => this.#addMoment(d, what));
  }

  /**
   * Whether "everything else" can be offered: a group narrows its events and
   * no group is already everything else.
   */
  get everythingElseApplies(): boolean {
    return elseTarget(this.state.moments) !== undefined;
  }

  get everythingElseReason(): Reason {
    return this.#reason((d) => this.#addEverythingElse(d));
  }

  #addEverythingElse(d: WorkspaceState): Reason {
    const target = elseTarget(d.moments);
    if (!target) return "Each kind of event here already has its everything else.";
    if (d.moments.length >= this.maxMoments)
      return `A chart has at most ${this.maxMoments} groups of events.`;
    if (d.moments.length >= MAX_OVERLAID_GROUPS)
      return "Everything else can go with at most two other groups.";
    d.moments.push({ key: freshKey(), what: target, everythingElse: true });
    return null;
  }

  /** Adds the complement of the other moments. */
  addEverythingElse(): Reason {
    return this.#commit((d) => this.#addEverythingElse(d));
  }

  removeMoment(index: number): Reason {
    return this.#commit((d) => {
      if (d.moments.length <= 1)
        return "A chart around an event has at least one group.";
      d.moments.splice(index, 1);
      if (d.moments.every((m) => m.everythingElse))
        d.moments = [{ key: freshKey(), what: d.moments[0].what }];
      retargetElse(d);
    });
  }

  /**
   * Changes a moment. Meal narrowings are dropped from a moment that is not
   * meals.
   */
  updateMoment(index: number, patch: MomentPatch): Reason {
    return this.#commit((d) => {
      const moment = d.moments[index];
      if (!moment) return "No such group.";
      if (moment.everythingElse)
        return "Everything else has no settings of its own.";
      Object.assign(moment, patch);
      for (const key of NARROWINGS)
        if (key in patch && patch[key] === undefined) delete moment[key];
      if (
        (patch.minutesSinceMeal && this.conditionReason("minutesSinceMeal")) ||
        (patch.insulinOnBoard && this.conditionReason("insulinOnBoard"))
      )
        return "Your access cannot read treatments, which this needs.";
      if (!anchor(moment.what).meal) {
        if (patch.grams || patch.foods || patch.foodCategory)
          return "Only meals can be narrowed by size or food.";
        delete moment.grams;
        delete moment.foods;
        delete moment.foodCategory;
      }
      retargetElse(d);
    });
  }

  setDayGroupPreset(preset: DayGroupPresetId): Reason {
    return this.setDayGroups(
      DAY_GROUP_PRESETS[preset].groups.map((g) => [...g])
    );
  }

  dayGroupPresetReason(preset: DayGroupPresetId): Reason {
    return this.#reason((d) => {
      d.dayGroups = DAY_GROUP_PRESETS[preset].groups.map((g) => [...g]);
    });
  }

  setDayGroups(groups: Weekday[][]): Reason {
    return this.#commit((d) => {
      if (groups.length === 0 || groups.some((g) => g.length === 0))
        return "Each group has at least one day.";
      if (groups.length > MAX_DAY_GROUPS)
        return `A chart has at most ${MAX_DAY_GROUPS} groups of days.`;
      d.dayGroups = groups.map((g) => [...g]);
    });
  }

  setEvent(patch: Partial<EventOptions>): Reason {
    if (patch.comparison && this.comparisonReason) return this.comparisonReason;
    if (patch.comparison !== undefined) this.comparisonDropped = false;
    return this.#commit((d) => {
      if (patch.before !== undefined && !this.beforeChoices.includes(patch.before))
        return "Choose one of the windows given.";
      if (patch.after !== undefined && !this.afterChoices.includes(patch.after))
        return "Choose one of the windows given.";
      d.event = { ...d.event, ...patch };
    });
  }

  setLeaveOut(id: LeaveOutId, on: boolean): Reason {
    return this.#commit((d) => {
      d.leaveOut[id] = on;
    });
  }

  setProfileName(name: string | null): Reason {
    return this.#commit((d) => {
      d.profileName = name?.trim() ? name.trim().slice(0, 100) : null;
    });
  }

  /** Replaces the state with a starter's; its range becomes the chart's own when `ownRange`. */
  load(given: Starter, ownRange = true) {
    // A starter held in a component's state is a proxy, which structuredClone refuses.
    const starter = $state.snapshot(given);
    const state = emptyState(this.#leaveOut);
    state.lens = starter.lens;
    state.range = ownRange ? starter.rangeHours : null;
    for (const s of starter.signals) {
      state.placed.push({
        id: s.id,
        marks: s.mark ? { [starter.lens]: s.mark } : {},
        relate: s.relate
          ? { [starter.lens]: { ...NO_RELATE, ...s.relate } }
          : {},
      });
      placeInFamilyLane(state, s.id);
    }
    if (starter.moments)
      state.moments = starter.moments.map((m) => ({
        ...structuredClone(m),
        key: freshKey(),
      }));
    if (starter.dayGroups)
      state.dayGroups = starter.dayGroups.map((g) => [...g]);
    if (starter.dayFilter) state.dayFilter = { ...starter.dayFilter };
    state.findLag = starter.findLag ?? false;
    state.event = { ...state.event, ...starter.event };
    this.state = state;
  }

  /** The chart as one sentence, in parts the UI can style. */
  caption(): CaptionPart[] {
    const s = this.state;
    const parts: CaptionPart[] = [];
    if (this.eventList) return this.#eventListCaption();
    if (s.lens === "against") {
      const pair = shownSignals(s);
      if (pair.length === 2)
        parts.push({
          kind: "signals",
          text: `${signalLabel(pair[1])} against ${lowerFirst(signalLabel(pair[0]))}`,
        });
      else if (pair.length === 1)
        parts.push({ kind: "signals", text: signalLabel(pair[0]) });
      parts.push({
        kind: "lens",
        text: s.findLag
          ? "each 15 minutes, with the lag between them"
          : "each 15 minutes",
      });
    } else {
      const names = [
        ...this.shown.map((id) => signalLabel(id)),
        ...this.combos.map((c) => comboLabel(c)),
      ];
      if (names.length > 0)
        parts.push({ kind: "signals", text: sentenceCase(names) });
      parts.push({ kind: "lens", text: this.#lensText() });
    }
    parts.push({ kind: "range", text: rangeText(this.rangeHours) });
    const groups = this.#groupsText();
    if (groups) parts.push({ kind: "groups", text: groups });
    const options = this.#optionsText();
    if (options) parts.push({ kind: "options", text: options });
    return parts;
  }

  /** The caption as one string. */
  captionText(): string {
    return this.caption()
      .map((p, i) => (i === 0 ? p.text : `${captionJoin(p, this.eventList)}${p.text}`))
      .join("");
  }

  /** The caption of a chart of the events one by one. */
  #eventListCaption(): CaptionPart[] {
    const s = this.state;
    const anchors = distinct(s.moments.map((m) => m.what));
    const phrase = joinAnd(anchors.map((a) => anchorPhrase(a)));
    let head: string;
    if (s.event.show === "each")
      head = `${outcomeLabel(this.up)} against ${lowerFirst(alongLabel(this.along))}`;
    else if (s.event.show === "spread")
      head = `How many ${phrase} had each ${lowerFirst(outcomeLabel(this.figure))}`;
    else {
      const verb = anchors.every((a) => isStartAnchor(a)) ? "start" : "happen";
      head = `When ${phrase} ${verb}, ${s.event.when === "hour" ? "by hour of day" : "by weekday"}`;
    }
    const parts: CaptionPart[] = [{ kind: "signals", text: head }];
    // The groups are worth naming when one narrows its events, or two share an anchor.
    const narrowed =
      s.moments.some((m) => isNarrowed(m) || m.everythingElse) ||
      anchors.length < s.moments.length;
    if (narrowed)
      parts.push({
        kind: "groups",
        text: `for ${s.moments
          .map((m) => momentText(m, true, this.clock, this.glucoseUnits))
          .join(" against ")}`,
      });
    parts.push({ kind: "range", text: rangeText(this.rangeHours) });
    const options = this.#optionsText();
    if (options) parts.push({ kind: "options", text: options });
    return parts;
  }

  #lensText(): string {
    const s = this.state;
    switch (s.lens) {
      case "timeline":
        return "as a timeline";
      case "typical_day":
        return "over a typical day";
      case "daily":
        return "day by day";
      case "event": {
        const anchors = distinct(
          s.moments.filter((m) => !m.everythingElse).map((m) => m.what)
        );
        const around = `around ${joinAnd(anchors.map((a) => anchorPhrase(a)))}`;
        const isDefault =
          s.event.before === EVENT_MINUTES_BEFORE &&
          s.event.after === EVENT_MINUTES_AFTER;
        return isDefault
          ? around
          : `${around}, ${lowerFirst(windowText(s.event.before, s.event.after))}`;
      }
      default:
        return "";
    }
  }

  #groupsText(): string | null {
    const s = this.state;
    if (this.groupsDays && s.dayFilter)
      return `${lowerFirst(dayFilterText(s.dayFilter, this.clock))} against all other days`;
    if (s.lens === "typical_day") {
      if (s.dayGroups.length === 1 && s.dayGroups[0].length === 7) return null;
      return s.dayGroups
        .map((g) => upperFirst(weekdaysText(g)))
        .join(" against ");
    }
    if (s.lens === "event") {
      const sameAnchor = distinct(s.moments.map((m) => m.what)).length === 1;
      if (s.moments.length === 1 && !isNarrowed(s.moments[0]) && !s.moments[0].everythingElse)
        return null;
      // A group that narrows nothing is named by its events, "all meals", not "all".
      return s.moments
        .map((m) =>
          momentText(m, !sameAnchor || (!isNarrowed(m) && !m.everythingElse), this.clock, this.glucoseUnits)
        )
        .join(" against ");
    }
    return null;
  }

  #optionsText(): string | null {
    const s = this.state;
    const parts: string[] = [];
    if (s.lens === "event") {
      if (s.event.isolatedOnly) parts.push("only events with nothing else near them");
      if (s.event.requireCorroboration && this.hasRises)
        parts.push("only rises the loop answered with extra insulin");
      if (s.event.comparison)
        parts.push(`against ${this.dates(s.event.comparison)}`);
    }
    if (this.leaveOutApplies) {
      const left = LEAVE_OUTS.filter((l) => s.leaveOut[l.id]).map((l) =>
        lowerFirst(leaveOutLabel(l.id))
      );
      if (left.length > 0) parts.push(`without ${joinAnd(left)}`);
      if (s.profileName) parts.push(`only while ${s.profileName} was active`);
    }
    return parts.length > 0 ? parts.join(", ") : null;
  }
}

/**
 * The kind of event an "everything else" group would take: the first kind a group narrows that has
 * no everything else yet (the API takes one for each kind), or undefined.
 */
function elseTarget(moments: Moment[]): AnchorId | undefined {
  return moments.find(
    (m) => isNarrowed(m) && !moments.some((o) => o.everythingElse && o.what === m.what)
  )?.what;
}

/**
 * Keeps each "everything else" group on a kind some other group takes: one left without one moves to
 * a kind that has none, else it goes.
 */
function retargetElse(d: WorkspaceState) {
  for (const other of [...d.moments]) {
    if (!other.everythingElse) continue;
    if (d.moments.some((m) => !m.everythingElse && m.what === other.what)) continue;
    const free = d.moments.find(
      (m) => !m.everythingElse && !d.moments.some((o) => o.everythingElse && o.what === m.what)
    );
    if (free) other.what = free.what;
    else d.moments.splice(d.moments.indexOf(other), 1);
  }
}

/** The dataset a view of the events one by one reads. */
function listDataset(show: EventShow): string {
  return show === "each" ? OUTCOMES_DATASET : show === "spread" ? SPREAD_DATASET : OCCURRENCE_DATASET;
}

function placeInFamilyLane(state: WorkspaceState, id: SignalId) {
  const family = signalOf(id).lane ?? signalOf(id).family;
  const lane = state.lanes.find((l) => l.family === family && !l.own);
  if (lane) {
    if (!lane.signals.includes(id)) lane.signals.push(id);
    return;
  }
  const order = FAMILY_LANES.map((f) => f.family);
  const at = state.lanes.findIndex(
    (l) => order.indexOf(l.family) > order.indexOf(family)
  );
  state.lanes.splice(at < 0 ? state.lanes.length : at, 0, {
    id: `lane-${family.toLowerCase()}`,
    family,
    own: false,
    signals: [id],
  });
}

const NARROWINGS = [
  "timeOfDay",
  "weekdays",
  "grams",
  "foods",
  "foodCategory",
  "minutesSinceMeal",
  "startGlucose",
  "insulinOnBoard",
] as const;

/** The object under `key`, or undefined when there is none. */
function objectAt(
  value: unknown,
  key: string
): Record<string, unknown> | undefined {
  if (!value || typeof value !== "object" || !(key in value)) return undefined;
  const found: unknown = Reflect.get(value, key);
  return found && typeof found === "object"
    ? Object.fromEntries(Object.entries(found))
    : undefined;
}

function distinct<T>(items: T[]): T[] {
  return items.filter((item, i) => items.indexOf(item) === i);
}

/** What goes before a caption part: a comma before groups and options, else a space; a comma before each part of an event list's caption. */
export function captionJoin(part: CaptionPart, eventList: boolean): string {
  if (eventList) return ", ";
  return part.kind === "groups" || part.kind === "options" ? ", " : " ";
}

export function lowerFirst(text: string): string {
  return text.charAt(0).toLowerCase() + text.slice(1);
}

export function upperFirst(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

