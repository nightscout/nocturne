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
export type AnchorId =
  | "Meal"
  | "CorrectionBolus"
  | "SensorStart"
  | "ExerciseStart"
  | "OverrideStart"
  | "TemporaryTargetStart"
  | "UnannouncedRise"
  | "LowStart"
  | "HighStart"
  | "SiteChange"
  | "SleepStart"
  | "Wake"
  | "LowTreatment"
  | "AnyBolus";
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

export const ANCHOR_MEAL: AnchorId = "Meal";
export const ANCHOR_RISE: AnchorId = "UnannouncedRise";
export const ANCHOR_CORRECTION: AnchorId = "CorrectionBolus";
export const ANCHOR_SENSOR: AnchorId = "SensorStart";
export const ANCHOR_LOW: AnchorId = "LowStart";
export const ANCHOR_HIGH: AnchorId = "HighStart";
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
  label: string;
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
  label: string;
  description: string;
}

export interface Anchor {
  id: AnchorId;
  label: string;
  /** The label inside a sentence: "around meals". */
  phrase: string;
  /** Meal narrowings (grams, foods, food category) apply only to meals. */
  meal: boolean;
  /** The heading the event menu lists it under. */
  group: string;
}

export interface RangePreset {
  id: RangePresetId;
  label: string;
  hours: number;
}

export interface FamilyLane {
  family: Family;
  /** The lane's share of the chart's height. */
  height: number;
}

/** One way a signal is drawn: the spec's mark and its bindings. */
export interface MarkChoice {
  mark: string;
  label: string;
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
  /** The unit of the field, as a key: two cells of one key share an axis. */
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
  label: string;
  /** The API's default, used when the catalogue gives none. */
  fallback: boolean;
}

/** A dataset a block of figures or a table reads, named in plain words. */
export interface Source {
  dataset: string;
  label: string;
}

export const SIGNALS: readonly Signal[] = [
  {
    id: "glucose",
    label: "Glucose",
    family: "Glucose",
    color: "--glucose-in-range",
  },
  {
    id: "target",
    label: "Target range",
    family: "Glucose",
    color: "--glucose-tight-range",
  },
  {
    id: "glucoseWide",
    label: "Glucose, 10 to 90 percent",
    family: "Glucose",
    color: "--glucose-in-range",
  },
  {
    id: "mean",
    label: "Mean glucose",
    family: "Glucose",
    color: "--glucose-in-range",
  },
  {
    id: "timeInRange",
    label: "Time in range",
    family: "Glucose",
    color: "--glucose-tight-range",
  },
  {
    id: "timeBelow",
    label: "Time below range",
    family: "Glucose",
    color: "--glucose-low",
  },
  {
    id: "timeAbove",
    label: "Time above range",
    family: "Glucose",
    color: "--glucose-high",
  },
  {
    id: "ranges",
    label: "Time in each range",
    family: "Glucose",
    color: "--glucose-in-range",
  },
  {
    id: "readings",
    label: "Readings",
    family: "Glucose",
    color: "--system-event-info",
  },
  {
    id: "iob",
    label: "Insulin on board",
    family: "Insulin",
    color: "--insulin",
  },
  {
    id: "basal",
    label: "Basal delivered",
    family: "Insulin",
    color: "--basal",
  },
  {
    id: "basalScheduled",
    label: "Basal scheduled",
    family: "Insulin",
    color: "--insulin-scheduled-basal",
  },
  {
    id: "boluses",
    label: "Boluses",
    family: "Insulin",
    color: "--insulin-bolus",
  },
  {
    id: "injections",
    label: "Long-acting injections",
    family: "Insulin",
    color: "--insulin-basal",
  },
  {
    id: "netLoop",
    label: "Insulin the loop added or removed",
    family: "Insulin",
    color: "--insulin-additional-basal",
  },
  {
    id: "totalInsulin",
    label: "Total insulin",
    family: "Insulin",
    color: "--insulin",
  },
  {
    id: "totalBolus",
    label: "Total bolus",
    family: "Insulin",
    color: "--insulin-bolus",
  },
  {
    id: "totalBasal",
    label: "Total basal",
    family: "Insulin",
    color: "--basal",
  },
  {
    id: "cob",
    label: "Carbs on board",
    family: "Food",
    color: "--carbs-on-board",
  },
  { id: "carbs", label: "Carbs", family: "Food", color: "--carbs" },
  {
    id: "reservoir",
    label: "Reservoir",
    family: "Devices",
    color: "--insulin-basal",
  },
  {
    id: "pumpBattery",
    label: "Pump battery",
    family: "Devices",
    color: "--pump-mode-boost",
  },
  {
    id: "uploaderBattery",
    label: "Uploader battery",
    family: "Devices",
    color: "--pump-mode-sleep",
  },
  {
    id: "settingBasal",
    label: "Basal rate in data and profile",
    family: "Settings",
    color: "--basal",
  },
  {
    id: "settingIsf",
    label: "ISF in data and profile",
    family: "Settings",
    color: "--insulin",
  },
  {
    id: "settingCarbRatio",
    label: "Carb ratio in data and profile",
    family: "Settings",
    color: "--carbs",
  },
  {
    id: "heartRate",
    label: "Heart rate",
    family: "Body",
    color: "--heart-rate",
  },
  { id: "steps", label: "Steps", family: "Body", color: "--steps" },
  {
    id: "sleep",
    label: "Sleep",
    family: "Body",
    color: "--pump-mode-sleep",
    lane: "Context",
  },
  {
    id: "pumpModes",
    label: "Pump modes",
    family: "Context",
    color: "--pump-mode-automatic",
  },
  {
    id: "profiles",
    label: "Profiles",
    family: "Context",
    color: "--pump-mode-liberty",
  },
  {
    id: "overrides",
    label: "Overrides",
    family: "Context",
    color: "--pump-mode-boost",
  },
  {
    id: "activities",
    label: "Activities",
    family: "Context",
    color: "--pump-mode-exercise",
  },
  {
    id: "tempBasals",
    label: "Temp basals",
    family: "Context",
    color: "--insulin-temp-basal",
  },
  {
    id: "bgChecks",
    label: "BG checks",
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
    label: "Timeline",
    description: "Each value at the time it occurred.",
  },
  {
    id: "typical_day",
    label: "Typical day",
    description:
      "All days put on top of one day, as the median and middle half.",
  },
  {
    id: "event",
    label: "Around an event",
    description:
      "The hours before and after each event, put on top of each other.",
  },
  {
    id: "daily",
    label: "Day by day",
    description: "One value for each day.",
  },
  {
    id: "against",
    label: "One against another",
    description: "Two signals as points, each 15 minutes, one along each axis.",
  },
];

export const ANCHOR_GROUPS = [
  "Food and insulin",
  "Glucose",
  "Activity and sleep",
  "Devices and modes",
] as const;

export const ANCHORS: readonly Anchor[] = [
  { id: "Meal", label: "Meals", phrase: "meals", meal: true, group: "Food and insulin" },
  {
    id: "CorrectionBolus",
    label: "Correction boluses",
    phrase: "correction boluses",
    meal: false,
    group: "Food and insulin",
  },
  { id: "AnyBolus", label: "Boluses", phrase: "boluses", meal: false, group: "Food and insulin" },
  {
    id: "UnannouncedRise",
    label: "Rises with no carbs",
    phrase: "rises with no carbs",
    meal: false,
    group: "Food and insulin",
  },
  { id: "LowStart", label: "Lows", phrase: "lows", meal: false, group: "Glucose" },
  { id: "HighStart", label: "Highs", phrase: "highs", meal: false, group: "Glucose" },
  {
    id: "LowTreatment",
    label: "Carbs taken in a low",
    phrase: "carbs taken in a low",
    meal: false,
    group: "Glucose",
  },
  { id: "ExerciseStart", label: "Exercise", phrase: "exercise", meal: false, group: "Activity and sleep" },
  { id: "SleepStart", label: "Sleep start", phrase: "sleep starts", meal: false, group: "Activity and sleep" },
  { id: "Wake", label: "Wake-up", phrase: "wake-ups", meal: false, group: "Activity and sleep" },
  {
    id: "SensorStart",
    label: "Sensor starts",
    phrase: "sensor starts",
    meal: false,
    group: "Devices and modes",
  },
  { id: "SiteChange", label: "Site changes", phrase: "site changes", meal: false, group: "Devices and modes" },
  { id: "OverrideStart", label: "Overrides", phrase: "overrides", meal: false, group: "Devices and modes" },
  {
    id: "TemporaryTargetStart",
    label: "Temporary targets",
    phrase: "temporary targets",
    meal: false,
    group: "Devices and modes",
  },
];

/** Anchors read from sleep sessions: they need the sleep permission. */
export const SLEEP_ANCHORS: readonly AnchorId[] = ["SleepStart", "Wake"];

export const RANGE_PRESETS: readonly RangePreset[] = [
  { id: "6h", label: "6 hours", hours: 6 },
  { id: "24h", label: "24 hours", hours: 24 },
  { id: "7d", label: "7 days", hours: 168 },
  { id: "14d", label: "14 days", hours: 336 },
  { id: "28d", label: "28 days", hours: 672 },
  { id: "90d", label: "90 days", hours: 2160 },
];

/** The report's range when a starter gives none. */
export const DEFAULT_REPORT_HOURS = 336;

export const WEEKDAYS: readonly { id: Weekday; label: string }[] = [
  { id: "Mon", label: "Monday" },
  { id: "Tue", label: "Tuesday" },
  { id: "Wed", label: "Wednesday" },
  { id: "Thu", label: "Thursday" },
  { id: "Fri", label: "Friday" },
  { id: "Sat", label: "Saturday" },
  { id: "Sun", label: "Sunday" },
];

export const DAY_GROUP_PRESETS = {
  everyDay: {
    label: "Every day",
    groups: [["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]],
  },
  weekdaysAndWeekend: {
    label: "Weekdays and the weekend",
    groups: [
      ["Mon", "Tue", "Wed", "Thu", "Fri"],
      ["Sat", "Sun"],
    ],
  },
  fridayAndWeekend: {
    label: "Monday to Thursday, Friday, and the weekend",
    groups: [["Mon", "Tue", "Wed", "Thu"], ["Fri"], ["Sat", "Sun"]],
  },
} as const satisfies Record<string, { label: string; groups: Weekday[][] }>;

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
    label: "Overrides",
    fallback: false,
  },
  {
    id: "temporaryTargets",
    parameter: "excludeTemporaryTargets",
    label: "Temporary targets",
    fallback: false,
  },
  {
    id: "exercise",
    parameter: "excludeExercise",
    label: "Exercise",
    fallback: false,
  },
  {
    id: "illness",
    parameter: "excludeIllness",
    label: "Illness",
    fallback: true,
  },
  {
    id: "openLoop",
    parameter: "excludeOpenLoop",
    label: "Manual mode",
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
  { dataset: "glucose_statistics", label: "Glucose statistics" },
  { dataset: "glucose_distribution", label: "Time in each glucose range" },
  { dataset: "device_status", label: "Device status" },
];

/** The datasets a table lists, named by what one row is. */
export const TABLE_SOURCES: readonly Source[] = [
  { dataset: "treatments", label: "Treatments" },
  { dataset: "daily_summary", label: "Each day" },
  { dataset: "event_response", label: "Each event" },
  { dataset: "glucose_series", label: "Each glucose reading" },
  { dataset: "basal_delivery", label: "Basal rates over time" },
  { dataset: "state_spans", label: "Modes, profiles and overrides" },
  { dataset: "glucose_distribution_by_hour", label: "Each hour of the day" },
  { dataset: "daily_profile", label: "Each 5 minutes of a typical day" },
  { dataset: "settings_evidence", label: "Settings against the data" },
  { dataset: "glucose_statistics", label: "Glucose statistics" },
];

export const COMPARISON_DATASET = "period_comparison";
export const OUTCOMES_DATASET = "event_outcomes";
export const SPREAD_DATASET = "event_outcome_spread";
export const OCCURRENCE_DATASET = "event_occurrence";

export interface OutcomeChoice {
  id: Outcome;
  label: string;
  /** Drawn only as a spread: as each event it lies along the axis. */
  spreadOnly?: boolean;
}

/** The figures of each event, in plain words. */
export const OUTCOMES: readonly OutcomeChoice[] = [
  { id: "peakRise", label: "Peak rise" },
  { id: "minutesToPeak", label: "Time to peak" },
  {
    id: "minutesBackToStart",
    label: "Time back to the start value",
  },
  { id: "nadir", label: "Lowest reading" },
  { id: "drop", label: "Drop" },
  { id: "minutesToNadir", label: "Time to the lowest reading" },
  { id: "glucoseAt2Hours", label: "Glucose 2 hours after" },
  { id: "netInsulin", label: "Net insulin" },
  { id: "gramsPerUnit", label: "Carbs for each unit" },
  {
    id: "startGlucose",
    label: "Glucose at the event",
    spreadOnly: true,
  },
];

export interface AlongChoice {
  id: Along;
  label: string;
}

/** Whether an anchor is the start of something that lasts (a low, exercise), by the API's naming. */
export function isStartAnchor(id: AnchorId): boolean {
  return id.endsWith("Start");
}

/** How the engine's summary of a group of days begins its count line ("Only days with a low: 4 of 29 days."). */
export const DAY_COUNT_LINE = "Only days";

/** The one axis no line goes through: the time of day wraps at midnight. */
export const ALONG_TIME_OF_DAY: Along = "timeOfDay";

/** What each event can lie along. */
export const ALONGS: readonly AlongChoice[] = [
  { id: "startGlucose", label: "Glucose at the event" },
  { id: "grams", label: "Carbs" },
  { id: "minutesSinceMeal", label: "Time since the last carbs" },
  { id: "iobAtEvent", label: "Insulin on board at the event" },
  { id: "timeOfDay", label: "Time of day" },
];

export const EVENT_SHOWS: readonly { id: EventShow; label: string }[] = [
  { id: "curve", label: "Over time" },
  { id: "each", label: "Each event" },
  { id: "spread", label: "Spread" },
  { id: "when", label: "When" },
];
export const EVENT_DATASET = "event_response";

const line = (x: string, y: string, label = "Line"): MarkChoice => ({
  mark: "line",
  label,
  bindings: { x, y: [y] },
});
const lines = (x: string, y: string[]): MarkChoice => ({
  mark: "line",
  label: "Lines",
  bindings: { x, y },
});
const area = (x: string, y: string): MarkChoice => ({
  mark: "area",
  label: "Area",
  bindings: { x, y: [y] },
});
const points = (x: string, y: string | string[]): MarkChoice => ({
  mark: "points",
  label: "Points",
  bindings: { x, y: Array.isArray(y) ? y : [y] },
});
const bars = (x: string, y: string): MarkChoice => ({
  mark: "bars",
  label: "Bars",
  bindings: { x, y: [y] },
});
const stacked = (x: string, y: string[]): MarkChoice => ({
  mark: "bars",
  label: "Stacked bars",
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
  label: "Median and middle half",
  bindings: { x, y: [mid], edges: [low, high], ...(series ? { series } : {}) },
});
const edges = (
  x: string,
  low: string,
  high: string,
  label: string,
  series?: string
): MarkChoice => ({
  mark: "band",
  label,
  bindings: { x, edges: [low, high], ...(series ? { series } : {}) },
});
const seriesLine = (y: string): MarkChoice => ({
  mark: "line",
  label: "Median",
  bindings: { x: "minute", y: [y], series: "series" },
});
const markers = (y: string): MarkChoice => ({
  mark: "markers",
  label: "Markers",
  bindings: { x: "time", y: [y] },
  noAxis: true,
});
const spans: MarkChoice = {
  mark: "spans",
  label: "Spans",
  bindings: { start: "start", end: "end", category: "kind" },
  noAxis: true,
};

const TIME_SMOOTH = [30, 60, 120, 240];
const FOLD_SMOOTH = [30, 60, 120];
const DAY_SMOOTH = [4320, 10080];

const GLUCOSE = "glucose";

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
      marks: [edges("time", "targetLow", "targetHigh", "Band")],
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
          label: "Lowest to highest",
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
        line("minuteOfDay", "glucoseP50", "Median"),
      ],
    },
    target: {
      ...profile("", GLUCOSE),
      marks: [edges("minuteOfDay", "targetLow", "targetHigh", "Band")],
      smooth: [],
      trend: false,
      combine: false,
    },
    glucoseWide: {
      ...profile("", GLUCOSE),
      marks: [
        edges("minuteOfDay", "glucoseP10", "glucoseP90", "10 to 90 percent"),
      ],
      smooth: [],
      trend: false,
      combine: false,
    },
    readings: {
      ...profile("readings", "count"),
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
    settingIsf: setting("isf", "isfObserved", "isfProfile", "mg/dL/U"),
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
          "10 to 90 percent",
          "series"
        ),
      ],
      fromStartMarks: [
        edges("minute", "deltaP10", "deltaP90", "10 to 90 percent", "series"),
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
    readings: { ...day("readings", "count", "bars"), smooth: [], trend: false },
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
