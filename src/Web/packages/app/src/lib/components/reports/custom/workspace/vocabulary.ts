// This vocabulary (signals, lenses, and which dataset draws each signal under each lens) will move
// into the API's report catalogue (/api/v4/reports/custom/catalogue), and the workspace will read it from there.

/**
 * The words the workspace builds a report from: signals, lenses, the events a
 * moment can be, range presets, the blocks a report stacks, and for each signal
 * and lens the dataset, fields and marks the spec draws it with.
 *
 * Identifiers the API reads (anchors, weekdays, dataset and field names) live
 * here, in a plain module: wuchale extracts capitalised string literals from
 * `.svelte` and `.svelte.ts` files as messages, and a translated anchor or
 * weekday would reach the spec in another language. `identifiers.test.ts`
 * keeps them out of those files.
 */

import { EventAnchorKind } from "#lib/api/index.js";

export type SignalId =
  | "glucose"
  | "target"
  | "glucoseWide"
  | "readings"
  | "timeInRange"
  | "timeBelow"
  | "timeAbove"
  | "ranges"
  | "mean"
  | "iob"
  | "basal"
  | "basalScheduled"
  | "boluses"
  | "injections"
  | "totalInsulin"
  | "totalBolus"
  | "totalBasal"
  | "netLoop"
  | "cob"
  | "carbs"
  | "reservoir"
  | "pumpBattery"
  | "uploaderBattery"
  | "settingBasal"
  | "settingIsf"
  | "settingCarbRatio"
  | "pumpModes"
  | "profiles"
  | "overrides"
  | "activities"
  | "tempBasals"
  | "bgChecks"
  | "heartRate"
  | "steps"
  | "sleep";
export type LensId = "timeline" | "typical_day" | "event" | "daily" | "against";
export type Family =
  | "Glucose"
  | "Insulin"
  | "Food"
  | "Devices"
  | "Settings"
  | "Body"
  | "Context";
export type AnchorId = EventAnchorKind;
export type Weekday = "Mon" | "Tue" | "Wed" | "Thu" | "Fri" | "Sat" | "Sun";
export type RangePresetId = "6h" | "24h" | "7d" | "14d" | "28d" | "90d";
export type LeaveOutId =
  | "overrides"
  | "temporaryTargets"
  | "exercise"
  | "illness"
  | "openLoop";
export type BlockKind = "chart" | "figures" | "table" | "comparison";
/** What a chart around an event shows: the curve over time, or the events one by one. */
export type EventShow = "curve" | "each" | "spread" | "when";
export type Outcome =
  | "peakRise"
  | "minutesToPeak"
  | "minutesBackToStart"
  | "drop"
  | "nadir"
  | "minutesToNadir"
  | "glucoseAt2Hours"
  | "startGlucose"
  | "netInsulin"
  | "gramsPerUnit";
export type Along =
  | "startGlucose"
  | "grams"
  | "minutesSinceMeal"
  | "iobAtEvent"
  | "timeOfDay";
export type WhenAxis = "hour" | "weekday";
export type CombineOp = "difference" | "ratio";

export const ANCHOR_MEAL: AnchorId = EventAnchorKind.Meal;
export const ANCHOR_RISE: AnchorId = EventAnchorKind.UnannouncedRise;
export const ANCHOR_CORRECTION: AnchorId = EventAnchorKind.CorrectionBolus;
export const ANCHOR_SENSOR: AnchorId = EventAnchorKind.SensorStart;
export const ANCHOR_LOW: AnchorId = EventAnchorKind.LowStart;
export const ANCHOR_HIGH: AnchorId = EventAnchorKind.HighStart;
export const WEEKDAY_IDS: readonly Weekday[] = [
  "Mon",
  "Tue",
  "Wed",
  "Thu",
  "Fri",
  "Sat",
  "Sun",
];
export const WORKDAYS: readonly Weekday[] = WEEKDAY_IDS.slice(0, 5);

export interface Signal {
  id: SignalId;
  family: Family;
  /**
   * A CSS custom property of the chart theme
   * (`@nocturne/charts/themes/nocturne.css`).
   */
  color: string;
  /** The lane it is drawn in, when not its family's. */
  lane?: Family;
}

export interface Lens {
  id: LensId;
}

export interface Anchor {
  id: AnchorId;
  /** Meal narrowings (grams, foods, food category) apply only to meals. */
  meal: boolean;
  /** The heading the event menu lists it under. */
  group: AnchorGroup;
}

export interface RangePreset {
  id: RangePresetId;
  hours: number;
}

export interface FamilyLane {
  family: Family;
  /** The lane's share of the chart's height. */
  height: number;
}

/** The name of a way to draw a signal; `markLabel` gives its words. */
export type MarkWord =
  | "line"
  | "lines"
  | "area"
  | "points"
  | "bars"
  | "stacked"
  | "medianBand"
  | "band"
  | "range"
  | "percentiles"
  | "median"
  | "markers"
  | "spans";

/** One way a signal is drawn: the spec's mark and its bindings. */
export interface MarkChoice {
  mark: string;
  /** The words the choice is offered with, as `markLabel` writes them. */
  word: MarkWord;
  bindings: Record<string, unknown>;
  /** Markers and spans take no vertical axis, so no unit. */
  noAxis?: boolean;
}

/** How one signal is drawn under one lens. */
export interface Cell {
  dataset: string;
  parameters?: Record<string, unknown>;
  /** The field a derived layer (smooth, trend, scatter, lag, combine) reads. */
  field: string;
  /**
   * The unit of the field, as a key: two cells of one key share an axis. `NO_UNIT` for a count the
   * engine gives no unit, which takes no unit of a track's two.
   */
  unit: string;
  /**
   * The first is the default. Empty under `against`, where the signal is a
   * scatter source.
   */
  marks: MarkChoice[];
  /** Glucose under `event`: the change from each event's start value. */
  fromStartMarks?: MarkChoice[];
  fromStartField?: string;
  /**
   * Rolling-mean (and mean-and-SD) windows, in minutes, the lens's axis takes;
   * empty when smoothing does not apply.
   */
  smooth: number[];
  trend: boolean;
  /** Whether the field can be one source of a difference or a ratio. */
  combine?: boolean;
  /** The dataset takes `dayOfWeek`, so a group of days narrows it. */
  days?: boolean;
  /** The dataset takes `days`: only the days that had something, or all others. */
  happened?: boolean;
  /** The dataset takes the leave-out parameters and `profileName`. */
  leaveOut?: boolean;
  /** A kind of state span: the lane draws every placed kind in one layer. */
  spanKind?: string;
}

export interface LeaveOut {
  id: LeaveOutId;
  /** The dataset parameter. */
  parameter: string;
  /** The API's default, used when the catalogue gives none. */
  fallback: boolean;
}

/** A dataset a block of figures or a table reads, named in plain words. */
export interface Source {
  dataset: string;
}

export const SIGNALS: readonly Signal[] = [
  {
    id: "glucose",
    family: "Glucose",
    color: "--glucose-in-range",
  },
  {
    id: "target",
    family: "Glucose",
    color: "--glucose-tight-range",
  },
  {
    id: "glucoseWide",
    family: "Glucose",
    color: "--glucose-in-range",
  },
  {
    id: "mean",
    family: "Glucose",
    color: "--glucose-in-range",
  },
  {
    id: "timeInRange",
    family: "Glucose",
    color: "--glucose-tight-range",
  },
  {
    id: "timeBelow",
    family: "Glucose",
    color: "--glucose-low",
  },
  {
    id: "timeAbove",
    family: "Glucose",
    color: "--glucose-high",
  },
  {
    id: "ranges",
    family: "Glucose",
    color: "--glucose-in-range",
  },
  {
    id: "readings",
    family: "Glucose",
    color: "--system-event-info",
  },
  {
    id: "iob",
    family: "Insulin",
    color: "--insulin",
  },
  {
    id: "basal",
    family: "Insulin",
    color: "--basal",
  },
  {
    id: "basalScheduled",
    family: "Insulin",
    color: "--insulin-scheduled-basal",
  },
  {
    id: "boluses",
    family: "Insulin",
    color: "--insulin-bolus",
  },
  {
    id: "injections",
    family: "Insulin",
    color: "--insulin-basal",
  },
  {
    id: "netLoop",
    family: "Insulin",
    color: "--insulin-additional-basal",
  },
  {
    id: "totalInsulin",
    family: "Insulin",
    color: "--insulin",
  },
  {
    id: "totalBolus",
    family: "Insulin",
    color: "--insulin-bolus",
  },
  {
    id: "totalBasal",
    family: "Insulin",
    color: "--basal",
  },
  {
    id: "cob",
    family: "Food",
    color: "--carbs-on-board",
  },
  { id: "carbs", family: "Food", color: "--carbs" },
  {
    id: "reservoir",
    family: "Devices",
    color: "--insulin-basal",
  },
  {
    id: "pumpBattery",
    family: "Devices",
    color: "--pump-mode-boost",
  },
  {
    id: "uploaderBattery",
    family: "Devices",
    color: "--pump-mode-sleep",
  },
  {
    id: "settingBasal",
    family: "Settings",
    color: "--basal",
  },
  {
    id: "settingIsf",
    family: "Settings",
    color: "--insulin",
  },
  {
    id: "settingCarbRatio",
    family: "Settings",
    color: "--carbs",
  },
  {
    id: "heartRate",
    family: "Body",
    color: "--heart-rate",
  },
  { id: "steps", family: "Body", color: "--steps" },
  {
    id: "sleep",
    family: "Body",
    color: "--activity-sleep",
    lane: "Context",
  },
  {
    id: "pumpModes",
    family: "Context",
    color: "--pump-mode-automatic",
  },
  {
    id: "profiles",
    family: "Context",
    color: "--pump-mode-liberty",
  },
  {
    id: "overrides",
    family: "Context",
    color: "--pump-mode-boost",
  },
  {
    id: "activities",
    family: "Context",
    color: "--pump-mode-exercise",
  },
  {
    id: "tempBasals",
    family: "Context",
    color: "--insulin-temp-basal",
  },
  {
    id: "bgChecks",
    family: "Context",
    color: "--glucose-in-range",
  },
];

export const FAMILY_LANES: readonly FamilyLane[] = [
  { family: "Glucose", height: 2 },
  { family: "Insulin", height: 0.75 },
  { family: "Food", height: 0.75 },
  { family: "Devices", height: 0.75 },
  { family: "Settings", height: 1 },
  { family: "Body", height: 0.75 },
  { family: "Context", height: 0.5 },
];

export const LENSES: readonly Lens[] = [
  {
    id: "timeline",
  },
  {
    id: "typical_day",
  },
  {
    id: "event",
  },
  {
    id: "daily",
  },
  {
    id: "against",
  },
];

export const ANCHOR_GROUPS = ["food", "glucose", "activity", "devices"] as const;
export type AnchorGroup = (typeof ANCHOR_GROUPS)[number];

export const ANCHORS: readonly Anchor[] = [
  { id: EventAnchorKind.Meal, meal: true, group: "food" },
  {
    id: EventAnchorKind.CorrectionBolus,
    meal: false,
    group: "food",
  },
  { id: EventAnchorKind.AnyBolus, meal: false, group: "food" },
  {
    id: EventAnchorKind.UnannouncedRise,
    meal: false,
    group: "food",
  },
  { id: EventAnchorKind.LowStart, meal: false, group: "glucose" },
  { id: EventAnchorKind.HighStart, meal: false, group: "glucose" },
  {
    id: EventAnchorKind.LowTreatment,
    meal: false,
    group: "glucose",
  },
  { id: EventAnchorKind.ExerciseStart, meal: false, group: "activity" },
  { id: EventAnchorKind.SleepStart, meal: false, group: "activity" },
  { id: EventAnchorKind.Wake, meal: false, group: "activity" },
  {
    id: EventAnchorKind.SensorStart,
    meal: false,
    group: "devices",
  },
  { id: EventAnchorKind.SiteChange, meal: false, group: "devices" },
  { id: EventAnchorKind.OverrideStart, meal: false, group: "devices" },
  {
    id: EventAnchorKind.TemporaryTargetStart,
    meal: false,
    group: "devices",
  },
];

/** Anchors read from sleep sessions: they need the sleep permission. */
export const SLEEP_ANCHORS: readonly AnchorId[] = [EventAnchorKind.SleepStart, EventAnchorKind.Wake];

export const RANGE_PRESETS: readonly RangePreset[] = [
  { id: "6h", hours: 6 },
  { id: "24h", hours: 24 },
  { id: "7d", hours: 168 },
  { id: "14d", hours: 336 },
  { id: "28d", hours: 672 },
  { id: "90d", hours: 2160 },
];

/** The report's range when a starter gives none. */
export const DEFAULT_REPORT_HOURS = 336;

export const WEEKDAYS: readonly { id: Weekday }[] = [
  { id: "Mon" },
  { id: "Tue" },
  { id: "Wed" },
  { id: "Thu" },
  { id: "Fri" },
  { id: "Sat" },
  { id: "Sun" },
];

export const DAY_GROUP_PRESETS = {
  everyDay: {
    groups: [["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]],
  },
  weekdaysAndWeekend: {
    groups: [
      ["Mon", "Tue", "Wed", "Thu", "Fri"],
      ["Sat", "Sun"],
    ],
  },
  fridayAndWeekend: {
    groups: [["Mon", "Tue", "Wed", "Thu"], ["Fri"], ["Sat", "Sun"]],
  },
} as const satisfies Record<string, { groups: Weekday[][] }>;

export type DayGroupPresetId = keyof typeof DAY_GROUP_PRESETS;

export type DayKind = "exercise" | "illness" | "low" | "lowOvernight" | "carbs";

/** What a day can have had, to draw those days against all other days. */
export const DAY_KINDS: readonly {
  id: DayKind;
  /** Reads treatments, so needs that permission. */
  treatments: boolean;
}[] = [
  { id: "exercise", treatments: true },
  { id: "illness", treatments: true },
  { id: "low", treatments: false },
  { id: "lowOvernight", treatments: false },
  { id: "carbs", treatments: true },
];

export const LEAVE_OUTS: readonly LeaveOut[] = [
  {
    id: "overrides",
    parameter: "excludeOverrides",
    fallback: false,
  },
  {
    id: "temporaryTargets",
    parameter: "excludeTemporaryTargets",
    fallback: false,
  },
  {
    id: "exercise",
    parameter: "excludeExercise",
    fallback: false,
  },
  {
    id: "illness",
    parameter: "excludeIllness",
    fallback: true,
  },
  {
    id: "openLoop",
    parameter: "excludeOpenLoop",
    fallback: true,
  },
];

/** The scatter and lag panels pair their sources on this grid. */
export const AGAINST_GRID_MINUTES = 15;

/** Event-lens windows; the API's defaults and choices, when the catalogue gives none. */
export const EVENT_MINUTES_BEFORE = 60;
export const EVENT_MINUTES_AFTER = 300;
export const EVENT_BEFORE_CHOICES = [0, 30, 60, 90, 120, 180, 240];
export const EVENT_AFTER_CHOICES = [120, 180, 240, 300, 360, 420, 480];

/** The datasets a block of figures reads, in the order the picker lists them. */
export const FIGURE_SOURCES: readonly Source[] = [
  { dataset: "glucose_statistics" },
  { dataset: "glucose_distribution" },
  { dataset: "device_status" },
];

/** The datasets a table lists, named by what one row is. */
export const TABLE_SOURCES: readonly Source[] = [
  { dataset: "treatments" },
  { dataset: "daily_summary" },
  { dataset: "event_response" },
  { dataset: "glucose_series" },
  { dataset: "basal_delivery" },
  { dataset: "state_spans" },
  { dataset: "glucose_distribution_by_hour" },
  { dataset: "daily_profile" },
  { dataset: "settings_evidence" },
  { dataset: "glucose_statistics" },
];

export const COMPARISON_DATASET = "period_comparison";
export const OUTCOMES_DATASET = "event_outcomes";
export const SPREAD_DATASET = "event_outcome_spread";
export const OCCURRENCE_DATASET = "event_occurrence";

export interface OutcomeChoice {
  id: Outcome;
  /** Drawn only as a spread: as each event it lies along the axis. */
  spreadOnly?: boolean;
}

/** The figures of each event, in plain words. */
export const OUTCOMES: readonly OutcomeChoice[] = [
  { id: "peakRise" },
  { id: "minutesToPeak" },
  {
    id: "minutesBackToStart",
  },
  { id: "nadir" },
  { id: "drop" },
  { id: "minutesToNadir" },
  { id: "glucoseAt2Hours" },
  { id: "netInsulin" },
  { id: "gramsPerUnit" },
  {
    id: "startGlucose",
    spreadOnly: true,
  },
];

export interface AlongChoice {
  id: Along;
}

/** Whether an anchor is the start of something that lasts (a low, exercise), by the API's naming. */
export function isStartAnchor(id: AnchorId): boolean {
  return id.endsWith("Start");
}


/** The one axis no line goes through: the time of day wraps at midnight. */
export const ALONG_TIME_OF_DAY: Along = "timeOfDay";

/** What each event can lie along. */
export const ALONGS: readonly AlongChoice[] = [
  { id: "startGlucose" },
  { id: "grams" },
  { id: "minutesSinceMeal" },
  { id: "iobAtEvent" },
  { id: "timeOfDay" },
];

export const EVENT_SHOWS: readonly { id: EventShow }[] = [
  { id: "curve" },
  { id: "each" },
  { id: "spread" },
  { id: "when" },
];
export const EVENT_DATASET = "event_response";

const line = (x: string, y: string, word: MarkWord = "line"): MarkChoice => ({
  mark: "line",
  word,
  bindings: { x, y: [y] },
});
const lines = (x: string, y: string[]): MarkChoice => ({
  mark: "line",
  word: "lines",
  bindings: { x, y },
});
const area = (x: string, y: string): MarkChoice => ({
  mark: "area",
  word: "area",
  bindings: { x, y: [y] },
});
const points = (x: string, y: string | string[]): MarkChoice => ({
  mark: "points",
  word: "points",
  bindings: { x, y: Array.isArray(y) ? y : [y] },
});
const bars = (x: string, y: string): MarkChoice => ({
  mark: "bars",
  word: "bars",
  bindings: { x, y: [y] },
});
const stacked = (x: string, y: string[]): MarkChoice => ({
  mark: "bars",
  word: "stacked",
  bindings: { x, y, stacked: true },
});
const band = (
  x: string,
  mid: string,
  low: string,
  high: string,
  series?: string
): MarkChoice => ({
  mark: "band",
  word: "medianBand",
  bindings: { x, y: [mid], edges: [low, high], ...(series ? { series } : {}) },
});
const edges = (
  x: string,
  low: string,
  high: string,
  word: MarkWord,
  series?: string
): MarkChoice => ({
  mark: "band",
  word,
  bindings: { x, edges: [low, high], ...(series ? { series } : {}) },
});
const seriesLine = (y: string): MarkChoice => ({
  mark: "line",
  word: "median",
  bindings: { x: "minute", y: [y], series: "series" },
});
const markers = (y: string): MarkChoice => ({
  mark: "markers",
  word: "markers",
  bindings: { x: "time", y: [y] },
  noAxis: true,
});
const spans: MarkChoice = {
  mark: "spans",
  word: "spans",
  bindings: { start: "start", end: "end", category: "kind" },
  noAxis: true,
};

const TIME_SMOOTH = [30, 60, 120, 240];
const FOLD_SMOOTH = [30, 60, 120];
const DAY_SMOOTH = [4320, 10080];

/** A field the engine gives no unit, such as a count of readings. */
export const NO_UNIT = "";

/** The tenant's glucose unit, mg/dL or mmol/L, as a unit key: the engine writes the field in it. */
const GLUCOSE = "glucose";
/** The tenant's glucose unit per unit of insulin: an insulin sensitivity. */
const GLUCOSE_PER_UNIT = "glucose/U";

const series = (field: string, unit: string): Cell => ({
  dataset: "glucose_series",
  field,
  unit,
  marks: [line("time", field), area("time", field)],
  smooth: TIME_SMOOTH,
  trend: true,
  combine: true,
});
const treatments = (type: string, field: string, unit: string): Cell => ({
  dataset: "treatments",
  parameters: { types: [type] },
  field,
  unit,
  marks: [markers(field), bars("time", field)],
  smooth: [],
  trend: false,
});
const device = (field: string, unit: string): Cell => ({
  dataset: "device_status",
  field,
  unit,
  marks: [line("time", field), points("time", field)],
  smooth: TIME_SMOOTH,
  trend: true,
});
const span = (kind: string): Cell => ({
  dataset: "state_spans",
  field: "",
  unit: "",
  marks: [spans],
  smooth: [],
  trend: false,
  spanKind: kind,
});
const profile = (field: string, unit: string): Cell => ({
  dataset: "daily_profile",
  field,
  unit,
  marks: [line("minuteOfDay", field), area("minuteOfDay", field)],
  smooth: FOLD_SMOOTH,
  trend: true,
  combine: true,
  days: true,
  happened: true,
  leaveOut: true,
});
const setting = (
  name: string,
  observed: string,
  inProfile: string,
  unit: string
): Cell => ({
  dataset: "settings_evidence",
  parameters: { setting: name },
  field: observed,
  unit,
  marks: [
    lines("startMinute", [observed, inProfile]),
    points("startMinute", [observed, inProfile]),
  ],
  smooth: [],
  trend: false,
  leaveOut: true,
});
const day = (field: string, unit: string, first: "bars" | "line"): Cell => ({
  dataset: "daily_summary",
  field,
  unit,
  marks:
    first === "bars"
      ? [bars("day", field), line("day", field), points("day", field)]
      : [line("day", field), points("day", field), bars("day", field)],
  smooth: DAY_SMOOTH,
  trend: true,
  combine: true,
  happened: true,
});
const event = (field: string, unit: string): Cell => ({
  dataset: "event_response",
  field,
  unit,
  marks: [seriesLine(field)],
  smooth: [],
  trend: false,
  leaveOut: true,
});
const against = (dataset: string, field: string, unit: string): Cell => ({
  dataset,
  field,
  unit,
  marks: [],
  smooth: [],
  trend: false,
});

/**
 * The signal x lens table. A signal missing from a lens cannot be drawn under
 * it.
 */
export const CELLS: Record<LensId, Partial<Record<SignalId, Cell>>> = {
  timeline: {
    glucose: {
      ...series("glucose", GLUCOSE),
      marks: [line("time", "glucose"), points("time", "glucose")],
    },
    target: {
      dataset: "glucose_series",
      field: "",
      unit: GLUCOSE,
      marks: [edges("time", "targetLow", "targetHigh", "band")],
      smooth: [],
      trend: false,
    },
    iob: series("iob", "U"),
    cob: series("cob", "g"),
    basal: {
      dataset: "basal_delivery",
      field: "delivered",
      unit: "U/h",
      marks: [area("time", "delivered"), line("time", "delivered")],
      smooth: TIME_SMOOTH,
      trend: true,
      combine: true,
    },
    basalScheduled: {
      dataset: "basal_delivery",
      field: "scheduled",
      unit: "U/h",
      marks: [line("time", "scheduled"), area("time", "scheduled")],
      smooth: [],
      trend: false,
    },
    boluses: treatments("bolus", "insulin", "U"),
    injections: treatments("basal_injection", "injectionUnits", "U"),
    carbs: treatments("carbs", "carbs", "g"),
    bgChecks: {
      ...treatments("bg_check", "bgCheck", GLUCOSE),
      marks: [markers("bgCheck"), points("time", "bgCheck")],
    },
    reservoir: device("reservoir", "U"),
    pumpBattery: device("pumpBattery", "%"),
    uploaderBattery: device("uploaderBattery", "%"),
    heartRate: {
      dataset: "heart_rate",
      field: "bpm",
      unit: "bpm",
      marks: [
        line("time", "bpm"),
        {
          mark: "band",
          word: "range",
          bindings: { x: "time", y: ["bpm"], edges: ["bpmMin", "bpmMax"] },
        },
      ],
      smooth: TIME_SMOOTH,
      trend: true,
      combine: true,
    },
    steps: {
      dataset: "steps",
      field: "steps",
      unit: "steps",
      marks: [bars("time", "steps"), line("time", "steps")],
      smooth: [],
      trend: false,
    },
    sleep: {
      dataset: "sleep",
      field: "",
      unit: "",
      marks: [spans],
      smooth: [],
      trend: false,
    },
    pumpModes: span("pump_mode"),
    profiles: span("profile"),
    overrides: span("override"),
    activities: span("activity"),
    tempBasals: span("temp_basal"),
  },
  typical_day: {
    glucose: {
      ...profile("glucoseP50", GLUCOSE),
      marks: [
        band("minuteOfDay", "glucoseP50", "glucoseP25", "glucoseP75"),
        line("minuteOfDay", "glucoseP50", "median"),
      ],
    },
    target: {
      ...profile("", GLUCOSE),
      marks: [edges("minuteOfDay", "targetLow", "targetHigh", "band")],
      smooth: [],
      trend: false,
      combine: false,
    },
    glucoseWide: {
      ...profile("", GLUCOSE),
      marks: [
        edges("minuteOfDay", "glucoseP10", "glucoseP90", "percentiles"),
      ],
      smooth: [],
      trend: false,
      combine: false,
    },
    readings: {
      ...profile("readings", NO_UNIT),
      marks: [
        line("minuteOfDay", "readings"),
        area("minuteOfDay", "readings"),
      ],
      smooth: [],
    },
    ranges: {
      dataset: "glucose_distribution_by_hour",
      field: "",
      unit: "%",
      marks: [
        stacked("hour", [
          "veryLow",
          "low",
          "tightRange",
          "aboveTightRange",
          "high",
          "veryHigh",
        ]),
      ],
      smooth: [],
      trend: false,
      days: true,
    },
    mean: {
      dataset: "glucose_distribution_by_hour",
      field: "mean",
      unit: GLUCOSE,
      marks: [line("hour", "mean"), bars("hour", "mean")],
      smooth: [],
      trend: true,
      days: true,
    },
    iob: profile("iob", "U"),
    cob: profile("cob", "g"),
    basal: profile("deliveredBasal", "U/h"),
    basalScheduled: {
      ...profile("scheduledBasal", "U/h"),
      marks: [
        line("minuteOfDay", "scheduledBasal"),
        area("minuteOfDay", "scheduledBasal"),
      ],
      smooth: [],
      trend: false,
      combine: false,
    },
    netLoop: {
      ...profile("netLoopCorrection", "U/h"),
      marks: [
        area("minuteOfDay", "netLoopCorrection"),
        line("minuteOfDay", "netLoopCorrection"),
      ],
      smooth: [],
      trend: false,
      combine: false,
    },
    settingBasal: setting("basal", "basalObserved", "basalProfile", "U/h"),
    settingIsf: setting("isf", "isfObserved", "isfProfile", GLUCOSE_PER_UNIT),
    settingCarbRatio: setting(
      "carb_ratio",
      "carbRatioObserved",
      "carbRatioProfile",
      "g/U"
    ),
  },
  event: {
    glucose: {
      ...event("glucoseP50", GLUCOSE),
      marks: [
        band("minute", "glucoseP50", "glucoseP25", "glucoseP75", "series"),
        seriesLine("glucoseP50"),
      ],
      fromStartField: "deltaP50",
      fromStartMarks: [
        band("minute", "deltaP50", "deltaP25", "deltaP75", "series"),
        seriesLine("deltaP50"),
      ],
    },
    glucoseWide: {
      ...event("", GLUCOSE),
      marks: [
        edges(
          "minute",
          "glucoseP10",
          "glucoseP90",
          "percentiles",
          "series"
        ),
      ],
      fromStartMarks: [
        edges("minute", "deltaP10", "deltaP90", "percentiles", "series"),
      ],
    },
    iob: event("iobP50", "U"),
    cob: event("cobP50", "g"),
    netLoop: event("netLoopCorrectionP50", "U/h"),
  },
  daily: {
    timeInRange: day("timeInRange", "%", "bars"),
    timeBelow: day("timeBelow", "%", "bars"),
    timeAbove: day("timeAbove", "%", "bars"),
    ranges: {
      dataset: "daily_summary",
      field: "",
      unit: "%",
      marks: [stacked("day", ["timeBelow", "timeInRange", "timeAbove"])],
      smooth: [],
      trend: false,
      happened: true,
    },
    mean: day("mean", GLUCOSE, "line"),
    readings: { ...day("readings", NO_UNIT, "bars"), smooth: [], trend: false },
    totalInsulin: day("totalInsulin", "U", "bars"),
    totalBolus: day("totalBolus", "U", "bars"),
    totalBasal: day("totalBasal", "U", "bars"),
    carbs: day("totalCarbs", "g", "bars"),
    sleep: {
      dataset: "sleep",
      field: "totalSleep",
      unit: "h",
      marks: [
        bars("night", "totalSleep"),
        stacked("night", ["deepSleep", "lightSleep", "remSleep", "awake"]),
      ],
      smooth: [],
      trend: false,
    },
  },
  against: {
    glucose: against("glucose_series", "glucose", GLUCOSE),
    iob: against("glucose_series", "iob", "U"),
    cob: against("glucose_series", "cob", "g"),
    basal: against("basal_delivery", "delivered", "U/h"),
    heartRate: against("heart_rate", "bpm", "bpm"),
  },
};

export function signal(id: SignalId): Signal {
  return SIGNALS.find((s) => s.id === id)!;
}

export function lens(id: LensId): Lens {
  return LENSES.find((l) => l.id === id)!;
}

export function anchor(id: AnchorId): Anchor {
  return ANCHORS.find((a) => a.id === id)!;
}

export function cellOf(lensId: LensId, id: SignalId): Cell | undefined {
  return CELLS[lensId][id];
}

/** The lenses that can draw the signal, in lens order. */
export function lensesOf(id: SignalId): LensId[] {
  return LENSES.map((l) => l.id).filter((l) => CELLS[l][id] !== undefined);
}
