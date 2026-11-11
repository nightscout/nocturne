/**
 * The words the workspace shows for the catalogue's vocabulary (signals, lenses, kinds of event,
 * views) and for its starting questions, keyed by id. The ids come from the API's catalogue and
 * `starters.ts`; every word is here, where wuchale extracts it. Each map is built per call, so it is
 * read in the current locale. An id with no word here shows as the id itself, and the vocabulary test
 * fails until it has one.
 */
import { EventAnchorKind } from "#lib/api/index.js";
import { formatLocale } from "#lib/utils/formatting.js";
import type { Weekday } from "./catalogue-vocabulary.js";

/** The word for `id` in `words`, or the id itself when the web has none yet. */
function wordOf(words: Record<string, string>, id: string): string {
  return Object.hasOwn(words, id) ? words[id] : id;
}


/** Each signal's name. */
export function signalLabels(): Record<string, string> {
  return {
    glucose: "Glucose",
    target: "Target range",
    glucoseWide: "Glucose, 10 to 90 percent",
    mean: "Mean glucose",
    timeInRange: "Time in range",
    timeBelow: "Time below range",
    timeAbove: "Time above range",
    ranges: "Time in each range",
    readings: "Readings",
    iob: "Insulin on board",
    basal: "Basal delivered",
    basalScheduled: "Basal scheduled",
    boluses: "Boluses",
    injections: "Long-acting injections",
    netLoop: "Insulin the loop added or removed",
    totalInsulin: "Total insulin",
    totalBolus: "Total bolus",
    totalBasal: "Total basal",
    cob: "Carbs on board",
    carbs: "Carbs",
    reservoir: "Reservoir",
    pumpBattery: "Pump battery",
    uploaderBattery: "Uploader battery",
    settingBasal: "Basal rate in data and profile",
    settingIsf: "ISF in data and profile",
    settingCarbRatio: "Carb ratio in data and profile",
    heartRate: "Heart rate",
    steps: "Steps",
    sleep: "Sleep",
    pumpModes: "Pump modes",
    profiles: "Profiles",
    overrides: "Overrides",
    activities: "Activities",
    tempBasals: "Temp basals",
    bgChecks: "BG checks",
  };
}

export function signalLabel(id: string): string {
  return wordOf(signalLabels(), id);
}

/** Each lens's name. */
export function lensLabels(): Record<string, string> {
  return {
    timeline: "Timeline",
    typical_day: "Typical day",
    event: "Around an event",
    daily: "Day by day",
    against: "One against another",
  };
}

export function lensLabel(id: string): string {
  return wordOf(lensLabels(), id);
}

/** What each lens does, in one sentence. */
export function lensDescriptions(): Record<string, string> {
  return {
    timeline: "Each value at the time it occurred.",
    typical_day: "All days put on top of one day, as the median and middle half.",
    event: "The hours before and after each event, put on top of each other.",
    daily: "One value for each day.",
    against: "Two signals as points, each 15 minutes, one along each axis.",
  };
}

export function lensDescription(id: string): string {
  return wordOf(lensDescriptions(), id);
}

/** Each kind of event, as a heading or a choice: "Meals". */
export function anchorLabels(): Record<string, string> {
  return {
    [EventAnchorKind.Meal]: "Meals",
    [EventAnchorKind.CorrectionBolus]: "Correction boluses",
    [EventAnchorKind.AnyBolus]: "Boluses",
    [EventAnchorKind.UnannouncedRise]: "Rises with no carbs",
    [EventAnchorKind.LowStart]: "Lows",
    [EventAnchorKind.HighStart]: "Highs",
    [EventAnchorKind.LowTreatment]: "Carbs taken in a low",
    [EventAnchorKind.ExerciseStart]: "Exercise",
    [EventAnchorKind.SleepStart]: "Sleep start",
    [EventAnchorKind.Wake]: "Wake-up",
    [EventAnchorKind.SensorStart]: "Sensor starts",
    [EventAnchorKind.SiteChange]: "Site changes",
    [EventAnchorKind.OverrideStart]: "Overrides",
    [EventAnchorKind.TemporaryTargetStart]: "Temporary targets",
  };
}

export function anchorLabel(id: string): string {
  return wordOf(anchorLabels(), id);
}

/** Each kind of event inside a sentence: "around meals". */
export function anchorPhrases(): Record<string, string> {
  return {
    [EventAnchorKind.Meal]: "meals",
    [EventAnchorKind.CorrectionBolus]: "correction boluses",
    [EventAnchorKind.AnyBolus]: "boluses",
    [EventAnchorKind.UnannouncedRise]: "rises with no carbs",
    [EventAnchorKind.LowStart]: "lows",
    [EventAnchorKind.HighStart]: "highs",
    [EventAnchorKind.LowTreatment]: "carbs taken in a low",
    [EventAnchorKind.ExerciseStart]: "exercise",
    [EventAnchorKind.SleepStart]: "sleep starts",
    [EventAnchorKind.Wake]: "wake-ups",
    [EventAnchorKind.SensorStart]: "sensor starts",
    [EventAnchorKind.SiteChange]: "site changes",
    [EventAnchorKind.OverrideStart]: "overrides",
    [EventAnchorKind.TemporaryTargetStart]: "temporary targets",
  };
}

export function anchorPhrase(id: string): string {
  return wordOf(anchorPhrases(), id);
}

/** The headings the event menu lists the kinds of event under. */
export function anchorGroupLabels(): Record<string, string> {
  return {
    food: "Food and insulin",
    glucose: "Glucose",
    activity: "Activity and sleep",
    devices: "Devices and modes",
  };
}

export function anchorGroupLabel(id: string): string {
  return wordOf(anchorGroupLabels(), id);
}

/** Each range preset. */
export function rangeLabels(): Record<string, string> {
  return {
    "6h": "6 hours",
    "24h": "24 hours",
    "7d": "7 days",
    "14d": "14 days",
    "28d": "28 days",
    "90d": "90 days",
  };
}

export function rangeLabel(id: string): string {
  return wordOf(rangeLabels(), id);
}

/** Each preset of groups of days. */
export function dayGroupPresetLabels(): Record<string, string> {
  return {
    everyDay: "Every day",
    weekdaysAndWeekend: "Weekdays and the weekend",
    fridayAndWeekend: "Monday to Thursday, Friday, and the weekend",
  };
}

export function dayGroupPresetLabel(id: string): string {
  return wordOf(dayGroupPresetLabels(), id);
}

/** Each kind of time a chart can leave out. */
export function leaveOutLabels(): Record<string, string> {
  return {
    overrides: "Overrides",
    temporaryTargets: "Temporary targets",
    exercise: "Exercise",
    illness: "Illness",
    openLoop: "Manual mode",
  };
}

export function leaveOutLabel(id: string): string {
  return wordOf(leaveOutLabels(), id);
}

/** Each figure of an event. */
export function outcomeLabels(): Record<string, string> {
  return {
    peakRise: "Peak rise",
    minutesToPeak: "Time to peak",
    minutesBackToStart: "Time back to the start value",
    nadir: "Lowest reading",
    drop: "Drop",
    minutesToNadir: "Time to the lowest reading",
    glucoseAt2Hours: "Glucose 2 hours after",
    netInsulin: "Net insulin",
    gramsPerUnit: "Carbs for each unit",
    startGlucose: "Glucose at the event",
  };
}

export function outcomeLabel(id: string): string {
  return wordOf(outcomeLabels(), id);
}

/** What each event can lie along. */
export function alongLabels(): Record<string, string> {
  return {
    startGlucose: "Glucose at the event",
    grams: "Carbs",
    minutesSinceMeal: "Time since the last carbs",
    iobAtEvent: "Insulin on board at the event",
    timeOfDay: "Time of day",
  };
}

export function alongLabel(id: string): string {
  return wordOf(alongLabels(), id);
}

/** Each way a chart around an event shows the events. */
export function eventShowLabels(): Record<string, string> {
  return {
    curve: "Over time",
    each: "Each event",
    spread: "Spread",
    when: "When",
  };
}

export function eventShowLabel(id: string): string {
  return wordOf(eventShowLabels(), id);
}

/** Each family of signals, as the rail's and the chips' headings. */
export function familyLabels(): Record<string, string> {
  return {
    Glucose: "Glucose",
    Insulin: "Insulin",
    Food: "Food",
    Devices: "Devices",
    Settings: "Settings",
    Body: "Body",
    Context: "Context",
  };
}

export function familyLabel(id: string): string {
  return wordOf(familyLabels(), id);
}

/** Each way a signal can be drawn. */
export function markLabels(): Record<string, string> {
  return {
    line: "Line",
    lines: "Lines",
    area: "Area",
    points: "Points",
    bars: "Bars",
    stacked: "Stacked bars",
    medianBand: "Median and middle half",
    band: "Band",
    range: "Lowest to highest",
    percentiles: "10 to 90 percent",
    median: "Median",
    markers: "Markers",
    spans: "Spans",
  };
}

export function markLabel(id: string): string {
  return wordOf(markLabels(), id);
}

/** The datasets a block of figures or a table reads, named by what they show. */
export function sourceLabels(): Record<string, string> {
  return {
    glucose_statistics: "Glucose statistics",
    glucose_distribution: "Time in each glucose range",
    device_status: "Device status",
    treatments: "Treatments",
    daily_summary: "Each day",
    event_response: "Each event",
    glucose_series: "Each glucose reading",
    basal_delivery: "Basal rates over time",
    state_spans: "Modes, profiles and overrides",
    glucose_distribution_by_hour: "Each hour of the day",
    daily_profile: "Each 5 minutes of a typical day",
    settings_evidence: "Settings against the data",
  };
}

/** A block's source in words; the dataset's own name when the workspace has none for it. */
export function sourceLabel(dataset: string): string {
  return wordOf(sourceLabels(), dataset);
}

/** The headings of the starting questions. */
export function starterGroupLabels(): Record<string, string> {
  return {
    glucose: "Glucose",
    lowsHighs: "Lows and highs",
    mealsInsulin: "Meals and insulin",
    bodyDevices: "Body and devices",
  };
}

export function starterGroupLabel(id: string): string {
  return wordOf(starterGroupLabels(), id);
}

/** Each starting question's title and one-line description; a description that names a time of day is written in the member's clock. */
export function starterWords(clock: (minute: number) => string): Record<string, { title: string; description: string }> {
  return {
    "last-24-hours": {
      title: "The last 24 hours",
      description: "Glucose, insulin, carbs and modes as they occurred.",
    },
    "dawn-rise": {
      title: "Is there a dawn rise?",
      description: "Glucose over a typical day, with the 10 to 90 percent band and the target range.",
    },
    "weekdays-weekend": {
      title: "Weekdays against the weekend",
      description: "Glucose over a typical day, Monday to Friday against Saturday and Sunday.",
    },
    "time-in-range-weekly": {
      title: "Is time in range changing week by week?",
      description: "Time in range for each day, its mean over 7 days and a trend line, over 90 days.",
    },
    "sensor-first-day": {
      title: "What does the first day of a sensor look like?",
      description: "Glucose from 1 hour before to 8 hours after each sensor start.",
    },
    "before-a-low": {
      title: "What happens before a low?",
      description: "Glucose, insulin on board and carbs on board from 4 hours before each low.",
    },
    "how-far-lows-go": {
      title: "How far do lows go, and what was on board?",
      description: "The lowest reading of each low against the insulin on board when it started.",
    },
    "when-lows-highs": {
      title: "What time of day do lows and highs start?",
      description: "Lows and highs counted by the hour of the day they started.",
    },
    "later-breakfast": {
      title: "What does a later breakfast do?",
      description: `Glucose after meals from ${clock(360)} to ${clock(510)} against meals from ${clock(510)} to ${clock(660)}.`,
    },
    "one-food": {
      title: "One food against everything else",
      description: "The change in glucose after meals with one food against all other meals.",
    },
    "start-and-peak": {
      title: "Does glucose at the start change the peak after a meal?",
      description: "The peak rise of each meal against the glucose when the meal started, with a line through the points.",
    },
    "meal-rise-spread": {
      title: "How much do meals raise glucose: no carbs in the 4 hours before against other meals",
      description: "How many meals had each peak rise, for meals with no carbs in the 4 hours before against all other meals.",
    },
    "meals-start-high": {
      title: "Meals that start in range against meals that start high",
      description: "The change in glucose after meals that start in range against meals that start above range.",
    },
    "exercise-no-carbs": {
      title: "Exercise with no carbs in the 4 hours before, against other exercise",
      description: "Glucose around exercise that starts 4 hours or more after the last carbs, against all other exercise.",
    },
    "corrections-night": {
      title: "Do corrections work differently at night?",
      description: `The change in glucose after correction boluses from ${clock(1320)} to ${clock(360)} against the rest of the day.`,
    },
    "insulin-glucose": {
      title: "How does glucose follow insulin?",
      description: "Glucose against insulin on board, and how long glucose takes to follow it.",
    },
    "loop-by-hour": {
      title: "How much does the loop add or remove, by hour?",
      description: "The insulin the loop added or removed over a typical day, beside the scheduled basal.",
    },
    "settings-data": {
      title: "What do the settings look like against the data?",
      description: "Basal rate, ISF and carb ratio as the data shows them, beside the values of the profile.",
    },
    "reservoir-batteries": {
      title: "Reservoir and batteries",
      description: "The pump reservoir and the pump and uploader batteries over the last 7 days.",
    },
    "days-with-a-low": {
      title: "Days with a low against all other days",
      description: "Glucose over a typical day, on the days with a low against all other days.",
    },
    "low-treated": {
      title: "What happens after a low is treated?",
      description: "Glucose and carbs on board from 30 minutes before to 3 hours after the first carbs taken in each low.",
    },
    "heart-rate-glucose": {
      title: "Heart rate and glucose",
      description: "Glucose and heart rate over the last 24 hours.",
    },
    "sleep-next-day": {
      title: "Sleep and the next day",
      description: "Hours of sleep each night beside the time in range of the day the night ended.",
    },
    "site-change": {
      title: "What does a site change do?",
      description: "Glucose from 1 hour before to 8 hours after each site change.",
    },
  };
}

/** A weekday's name in the app's locale; "short" for a toggle. */
export function weekdayName(day: Weekday, weekdays: readonly Weekday[], width: "long" | "short" = "long"): string {
  // `weekdays` is the catalogue's order, Monday first; 5 January 2026 is a Monday.
  const at = weekdays.indexOf(day);
  if (at < 0) return day;
  return new Intl.DateTimeFormat(formatLocale(), { weekday: width, timeZone: "UTC" }).format(Date.UTC(2026, 0, 5 + at));
}

/** The access scopes a part of a report reads, in words. */
export function scopeWords(): Record<string, string> {
  return {
    "glucose.read": "glucose",
    "treatments.read": "treatments",
    "food.read": "food",
    "therapy.read": "therapy settings",
    "devices.read": "devices",
    "heartrate.read": "heart rate",
    "sleep.read": "sleep",
    "stepcount.read": "steps",
  };
}

export function scopeWord(scope: string): string {
  return wordOf(scopeWords(), scope);
}

/** Why the reader cannot use something that reads `scopes`: its access lacks one of them. */
export function accessReason(scopes: readonly string[]): string {
  const words = scopes.map(scopeWord);
  return words.length > 0
    ? `Your access cannot read ${words.join(" or ")}, which this needs.`
    : "Your access cannot read the data this needs.";
}

/** The verb a "When" chart puts after a kind of event: lows "start", meals "happen". */
export function anchorVerbs(): Record<string, string> {
  return {
    [EventAnchorKind.Meal]: "happen",
    [EventAnchorKind.CorrectionBolus]: "happen",
    [EventAnchorKind.AnyBolus]: "happen",
    [EventAnchorKind.UnannouncedRise]: "start",
    [EventAnchorKind.LowStart]: "start",
    [EventAnchorKind.HighStart]: "start",
    [EventAnchorKind.LowTreatment]: "happen",
    [EventAnchorKind.ExerciseStart]: "start",
    [EventAnchorKind.SleepStart]: "start",
    [EventAnchorKind.Wake]: "happen",
    [EventAnchorKind.SensorStart]: "start",
    [EventAnchorKind.SiteChange]: "happen",
    [EventAnchorKind.OverrideStart]: "start",
    [EventAnchorKind.TemporaryTargetStart]: "start",
  };
}

/** "When lows start", "When meals happen": the verb each kind takes, "start" only when all do. */
export function whenPhrase(anchors: readonly string[], phrase: string): string {
  const verbs = anchorVerbs();
  const start = anchors.length > 0 && anchors.every((a) => verbs[a] === "start");
  return start ? `When ${phrase} start` : `When ${phrase} happen`;
}

/** What a "When" chart counts the events by. */
export function whenAxisLabels(): Record<string, string> {
  return { hour: "by hour of day", weekday: "by weekday" };
}

export function whenAxisLabel(id: string): string {
  return wordOf(whenAxisLabels(), id);
}

/** The menu labels of what a "When" chart counts by. */
export function whenAxisChoices(): Record<string, string> {
  return { hour: "By hour of day", weekday: "By weekday" };
}

export function whenAxisChoice(id: string): string {
  return wordOf(whenAxisChoices(), id);
}

/** What a day can have had, as a choice: the words of a day condition with no amount. */
export function dayKindLabels(): Record<string, string> {
  return {
    exercise: "Days with exercise",
    illness: "Days with illness",
    low: "Days with a low",
    lowOvernight: "Days with a low overnight",
    carbs: "Days by total carbs",
  };
}

export function dayKindLabel(id: string): string {
  return wordOf(dayKindLabels(), id);
}
