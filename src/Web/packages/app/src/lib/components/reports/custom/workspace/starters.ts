import type { Starter } from "./workspace.svelte.js";

export const STARTER_GROUPS = [
  "Glucose",
  "Lows and highs",
  "Meals and insulin",
  "Body and devices",
] as const;

/** A heading shows this many starters; the rest are behind "More questions". */
export const STARTERS_SHOWN = 6;

export const STARTERS: readonly Starter[] = [
  {
    id: "last-24-hours",
    group: "Glucose",
    title: "The last 24 hours",
    description: "Glucose, insulin, carbs and modes as they occurred.",
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
    group: "Glucose",
    title: "Is there a dawn rise?",
    description:
      "Glucose over a typical day, with the 10 to 90 percent band and the target range.",
    lens: "typical_day",
    rangeHours: 336,
    signals: [{ id: "glucoseWide" }, { id: "glucose" }, { id: "target" }],
  },
  {
    id: "weekdays-weekend",
    group: "Glucose",
    title: "Weekdays against the weekend",
    description:
      "Glucose over a typical day, Monday to Friday against Saturday and Sunday.",
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
    group: "Glucose",
    title: "Is time in range changing week by week?",
    description:
      "Time in range for each day, its mean over 7 days and a trend line, over 90 days.",
    lens: "daily",
    rangeHours: 2160,
    signals: [{ id: "timeInRange", relate: { smooth: 10080, trend: true } }],
  },
  {
    id: "sensor-first-day",
    group: "Body and devices",
    title: "What does the first day of a sensor look like?",
    description:
      "Glucose from 1 hour before to 8 hours after each sensor start.",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [{ what: "SensorStart" }],
    event: { before: 60, after: 480 },
  },
  {
    id: "before-a-low",
    group: "Lows and highs",
    title: "What happens before a low?",
    description:
      "Glucose, insulin on board and carbs on board from 4 hours before each low.",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }, { id: "iob" }, { id: "cob" }],
    moments: [{ what: "LowStart" }],
    event: { before: 240, after: 120 },
  },
  {
    id: "how-far-lows-go",
    group: "Lows and highs",
    title: "How far do lows go, and what was on board?",
    description:
      "The lowest reading of each low against the insulin on board when it started.",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [{ what: "LowStart" }],
    event: { show: "each", up: "nadir", along: "iobAtEvent" },
  },
  {
    id: "when-lows-highs",
    group: "Lows and highs",
    title: "What time of day do lows and highs start?",
    description: "Lows and highs counted by the hour of the day they started.",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [{ what: "LowStart" }, { what: "HighStart" }],
    event: { show: "when", when: "hour" },
  },
  {
    id: "later-breakfast",
    group: "Meals and insulin",
    title: "What does a later breakfast do?",
    description: (clock) =>
      `Glucose after meals from ${clock(360)} to ${clock(510)} against meals from ${clock(510)} to ${clock(660)}.`,
    lens: "event",
    rangeHours: 672,
    signals: [{ id: "glucose" }],
    moments: [
      { what: "Meal", timeOfDay: { fromMinute: 360, toMinute: 510 } },
      { what: "Meal", timeOfDay: { fromMinute: 510, toMinute: 660 } },
    ],
  },
  {
    id: "one-food",
    group: "Meals and insulin",
    title: "One food against everything else",
    description:
      "The change in glucose after meals with one food against all other meals.",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose", relate: { fromStart: true } }],
    moments: [
      { what: "Meal", foods: [] },
      { what: "Meal", everythingElse: true },
    ],
  },
  {
    id: "start-and-peak",
    group: "Meals and insulin",
    title: "Does glucose at the start change the peak after a meal?",
    description:
      "The peak rise of each meal against the glucose when the meal started, with a line through the points.",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [{ what: "Meal" }],
    event: { show: "each", up: "peakRise", along: "startGlucose" },
  },
  {
    id: "meal-rise-spread",
    group: "Meals and insulin",
    title:
      "How much do meals raise glucose: no carbs in the 4 hours before against other meals",
    description:
      "How many meals had each peak rise, for meals with no carbs in the 4 hours before against all other meals.",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [
      { what: "Meal", minutesSinceMeal: { min: 240 } },
      { what: "Meal", everythingElse: true },
    ],
    event: { show: "spread", figure: "peakRise" },
  },
  {
    id: "meals-start-high",
    group: "Meals and insulin",
    title: "Meals that start in range against meals that start high",
    description:
      "The change in glucose after meals that start in range against meals that start above range.",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose", relate: { fromStart: true } }],
    moments: [
      { what: "Meal", startGlucose: { min: 70, max: 180 } },
      { what: "Meal", startGlucose: { min: 181 } },
    ],
  },
  {
    id: "exercise-no-carbs",
    group: "Meals and insulin",
    title: "Exercise with no carbs in the 4 hours before, against other exercise",
    description:
      "Glucose around exercise that starts 4 hours or more after the last carbs, against all other exercise.",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [
      { what: "ExerciseStart", minutesSinceMeal: { min: 240 } },
      { what: "ExerciseStart", everythingElse: true },
    ],
  },
  {
    id: "corrections-night",
    group: "Meals and insulin",
    title: "Do corrections work differently at night?",
    description: (clock) =>
      `The change in glucose after correction boluses from ${clock(1320)} to ${clock(360)} against the rest of the day.`,
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose", relate: { fromStart: true } }],
    moments: [
      {
        what: "CorrectionBolus",
        timeOfDay: { fromMinute: 1320, toMinute: 360 },
      },
      {
        what: "CorrectionBolus",
        timeOfDay: { fromMinute: 360, toMinute: 1320 },
      },
    ],
  },
  {
    id: "insulin-glucose",
    group: "Meals and insulin",
    title: "How does glucose follow insulin?",
    description:
      "Glucose against insulin on board, and how long glucose takes to follow it.",
    lens: "against",
    rangeHours: 336,
    signals: [{ id: "iob" }, { id: "glucose" }],
    findLag: true,
  },
  {
    id: "loop-by-hour",
    group: "Meals and insulin",
    title: "How much does the loop add or remove, by hour?",
    description:
      "The insulin the loop added or removed over a typical day, beside the scheduled basal.",
    lens: "typical_day",
    rangeHours: 672,
    signals: [{ id: "netLoop" }, { id: "basalScheduled" }],
  },
  {
    id: "settings-data",
    group: "Body and devices",
    title: "What do the settings look like against the data?",
    description:
      "Basal rate, ISF and carb ratio as the data shows them, beside the values of the profile.",
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
    group: "Body and devices",
    title: "Reservoir and batteries",
    description:
      "The pump reservoir and the pump and uploader batteries over the last 7 days.",
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
    group: "Glucose",
    title: "Days with a low against all other days",
    description:
      "Glucose over a typical day, on the days with a low against all other days.",
    lens: "typical_day",
    rangeHours: 672,
    signals: [{ id: "glucose" }],
    dayFilter: { kind: "low" },
  },
  {
    id: "low-treated",
    group: "Lows and highs",
    title: "What happens after a low is treated?",
    description:
      "Glucose and carbs on board from 30 minutes before to 3 hours after the first carbs taken in each low.",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }, { id: "cob" }],
    moments: [{ what: "LowTreatment" }],
    event: { before: 30, after: 180 },
  },
  {
    id: "heart-rate-glucose",
    group: "Body and devices",
    title: "Heart rate and glucose",
    description: "Glucose and heart rate over the last 24 hours.",
    lens: "timeline",
    rangeHours: 24,
    signals: [{ id: "glucose" }, { id: "target" }, { id: "heartRate" }],
  },
  {
    id: "sleep-next-day",
    group: "Body and devices",
    title: "Sleep and the next day",
    description:
      "Hours of sleep each night beside the time in range of the day the night ended.",
    lens: "daily",
    rangeHours: 672,
    signals: [{ id: "sleep" }, { id: "timeInRange" }],
  },
  {
    id: "site-change",
    group: "Body and devices",
    title: "What does a site change do?",
    description: "Glucose from 1 hour before to 8 hours after each site change.",
    lens: "event",
    rangeHours: 2160,
    signals: [{ id: "glucose" }],
    moments: [{ what: "SiteChange" }],
    event: { before: 60, after: 480 },
  },
];

export function starter(id: string): Starter | undefined {
  return STARTERS.find((s) => s.id === id);
}
