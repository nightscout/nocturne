import { EventAnchorKind } from "#lib/api/index.js";
import type { Starter } from "./workspace.svelte.js";

export const STARTER_GROUPS = ["glucose", "lowsHighs", "mealsInsulin", "bodyDevices"] as const;
export type StarterGroup = (typeof STARTER_GROUPS)[number];

/** A heading shows this many starters; the rest are behind "More questions". */
export const STARTERS_SHOWN = 6;

export const STARTERS: readonly Starter[] = [
  {
    id: "last-24-hours",
    group: "glucose",
    lens: "timeline",
    rangeHours: 24,
    signals: [
      { id: "glucose" },
      { id: "target" },
      { id: "iob" },
      { id: "basal" },
      { id: "boluses" },
      { id: "cob" },
      { id: "carbs" },
      { id: "pumpModes" },
      { id: "overrides" },
    ],
  },
  {
    id: "dawn-rise",
    group: "glucose",
    lens: "typical_day",
    rangeHours: 336,
    signals: [{ id: "glucoseWide" }, { id: "glucose" }, { id: "target" }],
  },
  {
    id: "weekdays-weekend",
    group: "glucose",
    lens: "typical_day",
    rangeHours: 672,
    signals: [{ id: "glucose" }],
    dayGroups: [
      ["Mon", "Tue", "Wed", "Thu", "Fri"],
      ["Sat", "Sun"],
    ],
  },
  {
    id: "time-in-range-weekly",
    group: "glucose",
    lens: "daily",
    rangeHours: 2160,
    signals: [{ id: "timeInRange", relate: { smooth: 10080, trend: true } }],
  },
  {
    id: "sensor-first-day",
    group: "bodyDevices",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [{ what: EventAnchorKind.SensorStart }],
    event: { before: 60, after: 480 },
  },
  {
    id: "before-a-low",
    group: "lowsHighs",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }, { id: "iob" }, { id: "cob" }],
    moments: [{ what: EventAnchorKind.LowStart }],
    event: { before: 240, after: 120 },
  },
  {
    id: "how-far-lows-go",
    group: "lowsHighs",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [{ what: EventAnchorKind.LowStart }],
    event: { show: "each", up: "nadir", along: "iobAtEvent" },
  },
  {
    id: "when-lows-highs",
    group: "lowsHighs",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [{ what: EventAnchorKind.LowStart }, { what: EventAnchorKind.HighStart }],
    event: { show: "when", when: "hour" },
  },
  {
    id: "later-breakfast",
    group: "mealsInsulin",
    lens: "event",
    rangeHours: 672,
    signals: [{ id: "glucose" }],
    moments: [
      { what: EventAnchorKind.Meal, timeOfDay: { fromMinute: 360, toMinute: 510 } },
      { what: EventAnchorKind.Meal, timeOfDay: { fromMinute: 510, toMinute: 660 } },
    ],
  },
  {
    id: "one-food",
    group: "mealsInsulin",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose", relate: { fromStart: true } }],
    moments: [
      { what: EventAnchorKind.Meal, foods: [] },
      { what: EventAnchorKind.Meal, everythingElse: true },
    ],
  },
  {
    id: "start-and-peak",
    group: "mealsInsulin",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [{ what: EventAnchorKind.Meal }],
    event: { show: "each", up: "peakRise", along: "startGlucose" },
  },
  {
    id: "meal-rise-spread",
    group: "mealsInsulin",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [
      { what: EventAnchorKind.Meal, minutesSinceMeal: { min: 240 } },
      { what: EventAnchorKind.Meal, everythingElse: true },
    ],
    event: { show: "spread", figure: "peakRise" },
  },
  {
    id: "meals-start-high",
    group: "mealsInsulin",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose", relate: { fromStart: true } }],
    moments: [
      { what: EventAnchorKind.Meal, startGlucose: { min: 70, max: 180 } },
      { what: EventAnchorKind.Meal, startGlucose: { min: 181 } },
    ],
  },
  {
    id: "exercise-no-carbs",
    group: "mealsInsulin",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [
      { what: EventAnchorKind.ExerciseStart, minutesSinceMeal: { min: 240 } },
      { what: EventAnchorKind.ExerciseStart, everythingElse: true },
    ],
  },
  {
    id: "corrections-night",
    group: "mealsInsulin",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose", relate: { fromStart: true } }],
    moments: [
      {
        what: EventAnchorKind.CorrectionBolus,
        timeOfDay: { fromMinute: 1320, toMinute: 360 },
      },
      {
        what: EventAnchorKind.CorrectionBolus,
        timeOfDay: { fromMinute: 360, toMinute: 1320 },
      },
    ],
  },
  {
    id: "insulin-glucose",
    group: "mealsInsulin",
    lens: "against",
    rangeHours: 336,
    signals: [{ id: "iob" }, { id: "glucose" }],
    findLag: true,
  },
  {
    id: "loop-by-hour",
    group: "mealsInsulin",
    lens: "typical_day",
    rangeHours: 672,
    signals: [{ id: "netLoop" }, { id: "basalScheduled" }],
  },
  {
    id: "settings-data",
    group: "bodyDevices",
    lens: "typical_day",
    rangeHours: 672,
    signals: [
      { id: "settingBasal" },
      { id: "settingIsf" },
      { id: "settingCarbRatio" },
    ],
  },
  {
    id: "reservoir-batteries",
    group: "bodyDevices",
    lens: "timeline",
    rangeHours: 168,
    signals: [
      { id: "reservoir" },
      { id: "pumpBattery" },
      { id: "uploaderBattery" },
    ],
  },
  {
    id: "days-with-a-low",
    group: "glucose",
    lens: "typical_day",
    rangeHours: 672,
    signals: [{ id: "glucose" }],
    dayFilter: { kind: "low" },
  },
  {
    id: "low-treated",
    group: "lowsHighs",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }, { id: "cob" }],
    moments: [{ what: EventAnchorKind.LowTreatment }],
    event: { before: 30, after: 180 },
  },
  {
    id: "heart-rate-glucose",
    group: "bodyDevices",
    lens: "timeline",
    rangeHours: 24,
    signals: [{ id: "glucose" }, { id: "target" }, { id: "heartRate" }],
  },
  {
    id: "sleep-next-day",
    group: "bodyDevices",
    lens: "daily",
    rangeHours: 672,
    signals: [{ id: "sleep" }, { id: "timeInRange" }],
  },
  {
    id: "site-change",
    group: "bodyDevices",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [{ what: EventAnchorKind.SiteChange }],
    event: { before: 60, after: 480 },
  },
];

export function starter(id: string): Starter | undefined {
  return STARTERS.find((s) => s.id === id);
}
