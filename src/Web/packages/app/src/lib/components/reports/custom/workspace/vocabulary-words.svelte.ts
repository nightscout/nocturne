/**
 * The words the workspace shows for the catalogue's vocabulary (signals, lenses, kinds of event,
 * views) and for its starting questions, keyed by id. The ids come from the API's catalogue and
 * `starters.ts`; every word is here, where wuchale extracts it. Each map is built per call, so it is
 * read in the current locale. An id with no word here shows as the id itself, and the vocabulary test
 * fails until it has one.
 */
import { EventAnchorKind } from "#lib/api/index.js";
import { formatList, formatLocale } from "#lib/utils/formatting.js";
import { withEnglishWords } from "#lib/navigation/report-navigation.svelte.js";
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
    meterGlucose: "Meter readings from the pump",
    trendRate: "Glucose rate of change",
    glucoseEpisodes: "Lows and highs",
    labHbA1c: "Lab HbA1c",
    sensorChanges: "Sensor changes",
    siteChanges: "Site and insulin changes",
    pumpEvents: "Pump events",
    pumpConnection: "Pump connection",
    trackers: "Trackers",
    sleepStages: "Sleep stages",
    sleepScore: "Sleep score",
    sleepEfficiency: "Sleep efficiency",
    sleepLatency: "Time to fall asleep",
    restlessPeriods: "Restless periods",
    sleepHeartRate: "Heart rate each night",
    sleepHrv: "HRV each night",
    sleepSpo2: "Blood oxygen each night",
    sleepBreathing: "Breathing rate each night",
    heartRateInSleep: "Heart rate in sleep",
    hrvInSleep: "HRV in sleep",
    spo2InSleep: "Blood oxygen in sleep",
    breathingInSleep: "Breathing rate in sleep",
    motion: "Motion",
    weight: "Weight",
    bodyFat: "Body fat",
    leanMass: "Lean mass",
    temporaryTargets: "Temporary targets",
    alertPeriods: "Alert periods",
    alertsRaised: "Alerts raised",
    doNotDisturb: "Do not disturb",
    deviceAlarms: "Device alarms",
    dataExclusions: "Data left out",
    compressionLows: "Compression lows",
    sensorNoise: "Sensor noise",
  };
}

export function signalLabel(id: string): string {
  return wordOf(signalLabels(), id);
}

/** What each signal is, in one line, under its name in the signal catalogue. */
export function signalDescriptions(): Record<string, string> {
  return {
    glucose: "Each sensor reading.",
    target: "The low and high limits of your target range.",
    glucoseWide: "The band that holds 80 percent of the readings.",
    mean: "The mean of the readings.",
    timeInRange: "The percent of readings in the target range.",
    timeBelow: "The percent of readings below the target range.",
    timeAbove: "The percent of readings above the target range.",
    ranges: "The percent of readings in each glucose range.",
    readings: "How many sensor readings there are. Gaps show missing data.",
    iob: "The insulin that is still active.",
    basal: "The basal insulin the pump gave.",
    basalScheduled: "The basal rate of the profile.",
    boluses: "Each bolus and its units.",
    injections: "Each injection of long-acting insulin.",
    netLoop: "The insulin the loop added to or removed from the scheduled basal.",
    totalInsulin: "All insulin given in the day.",
    totalBolus: "All bolus insulin given in the day.",
    totalBasal: "All basal insulin given in the day.",
    cob: "The carbs that are not yet absorbed.",
    carbs: "Each entry of carbs and its grams.",
    reservoir: "The units of insulin left in the pump.",
    pumpBattery: "The charge of the pump battery.",
    uploaderBattery: "The charge of the phone or device that uploads the data.",
    settingBasal: "The basal rate the data shows, beside the rate of the profile.",
    settingIsf: "The insulin sensitivity the data shows, beside the value of the profile.",
    settingCarbRatio: "The carb ratio the data shows, beside the value of the profile.",
    heartRate: "Beats each minute.",
    steps: "The steps you took.",
    sleep: "Each sleep session and its stages.",
    pumpModes: "When the pump was in automatic, manual or other modes.",
    profiles: "Which profile was active.",
    overrides: "Each override.",
    activities: "Exercise, illness and travel that you marked.",
    tempBasals: "Each temp basal and its rate.",
    bgChecks: "Each meter reading.",
    meterGlucose: "Each meter reading that the pump or uploader sent.",
    trendRate: "How fast glucose changed, as the sensor reported it.",
    glucoseEpisodes: "Each low and high that lasted 15 minutes or more.",
    labHbA1c: "Each HbA1c result from a lab.",
    sensorChanges: "Each sensor start, change and stop.",
    siteChanges: "Each site, pod, reservoir and insulin change.",
    pumpEvents: "Each pump suspend, resume and battery change.",
    pumpConnection: "When the pump was connected or disconnected.",
    trackers: "Each tracked sensor, site or other item, and how many days it lasted.",
    sleepStages: "The stages of sleep in each night: awake, REM, light and deep.",
    sleepScore: "The score that the device gave each night.",
    sleepEfficiency: "The percent of each night that you were asleep.",
    sleepLatency: "The minutes from bed to sleep, each night.",
    restlessPeriods: "How many restless periods each night had.",
    sleepHeartRate: "The mean and lowest heart rate of each night.",
    sleepHrv: "The mean heart rate variability of each night.",
    sleepSpo2: "The mean blood oxygen of each night.",
    sleepBreathing: "The mean breathing rate of each night.",
    heartRateInSleep: "Heart rate that the wearable recorded during sleep.",
    hrvInSleep: "Heart rate variability that the wearable recorded during sleep.",
    spo2InSleep: "Blood oxygen that the wearable recorded during sleep.",
    breathingInSleep: "Breathing rate that the wearable recorded during sleep.",
    motion: "Walking, still or in a vehicle, as the phone found it.",
    weight: "Each body weight record.",
    bodyFat: "The percent of body weight that is fat.",
    leanMass: "The body weight that is not fat.",
    temporaryTargets: "Each temporary target.",
    alertPeriods: "Each period when the condition of an alert was true.",
    alertsRaised: "Each alert that Nocturne raised, and its snoozes.",
    doNotDisturb: "Each do-not-disturb period.",
    deviceAlarms: "Each alarm, warning and notice from the pump, sensor or uploader.",
    dataExclusions: "The periods that you left out of the glucose data.",
    compressionLows: "Each false low at night from pressure on the sensor.",
    sensorNoise: "The noise level of each sensor reading, from 0 to 4.",
  };
}

export function signalDescription(id: string): string {
  return Object.hasOwn(signalDescriptions(), id) ? signalDescriptions()[id] : "";
}

/** The headings of the signal catalogue's sections. */
export function sectionLabels(): Record<string, string> {
  return {
    glucose: "Glucose",
    insulin: "Insulin",
    food: "Food",
    loop: "Loop and automation",
    pumpDevices: "Pump and devices",
    sleep: "Sleep",
    activityBody: "Activity and body",
    settings: "Settings",
    context: "Context",
    alerts: "Alerts",
    dataQuality: "Data quality",
  };
}

export function sectionLabel(id: string): string {
  return wordOf(sectionLabels(), id);
}

/** The headings of the groups inside each section of the signal catalogue. */
export function signalGroupLabels(): Record<string, string> {
  return {
    sensor: "Sensor",
    meter: "Meter",
    ranges: "Time in ranges",
    statistics: "Statistics",
    onBoard: "On board",
    bolus: "Bolus",
    basal: "Basal",
    totals: "Totals",
    carbs: "Carbs",
    corrections: "Insulin changes",
    modes: "Modes",
    pump: "Pump",
    phone: "Phone and uploader",
    sessions: "Sessions",
    heart: "Heart",
    movement: "Movement",
    againstData: "Settings against the data",
    profiles: "Profiles",
    marked: "Marked periods",
    coverage: "Coverage",
    lab: "Lab results",
    consumables: "Consumables",
    nightly: "Each night",
    body: "Body during sleep",
    weight: "Weight",
    alertRules: "Your alerts",
    deviceAlarms: "Device alarms",
    quiet: "Do not disturb",
  };
}

export function signalGroupLabel(id: string): string {
  return wordOf(signalGroupLabels(), id);
}

const ENGLISH_SEARCH_TERMS: Record<string, string[]> = {
  bg: ["BG"],
  bloodSugar: ["blood sugar"],
  sensor: ["sensor"],
  cgm: ["CGM"],
  targetRange: ["target"],
  percentile: ["percentile"],
  meter: ["meter"],
  fingerstick: ["fingerstick"],
  tir: ["TIR"],
  tbr: ["TBR"],
  tar: ["TAR"],
  range: ["range"],
  low: ["low", "hypo"],
  high: ["high", "hyper"],
  average: ["average"],
  iob: ["IOB"],
  insulin: ["insulin"],
  bolus: ["bolus"],
  dose: ["dose"],
  rate: ["rate"],
  tempBasal: ["temp basal"],
  profile: ["profile"],
  mdi: ["MDI"],
  pen: ["pen"],
  tdd: ["TDD"],
  cob: ["COB"],
  meal: ["meal"],
  food: ["food"],
  loop: ["loop"],
  automation: ["automation", "closed loop"],
  pump: ["pump"],
  pod: ["pod"],
  battery: ["battery"],
  phone: ["phone", "uploader"],
  night: ["night"],
  bed: ["bed"],
  hr: ["HR"],
  pulse: ["pulse"],
  exercise: ["exercise"],
  walking: ["walking"],
  isf: ["ISF"],
  sensitivity: ["sensitivity"],
  icr: ["ICR", "carb ratio"],
  override: ["override"],
  illness: ["illness"],
  gaps: ["gaps", "missing data"],
  sage: ["SAGE", "sensor age"],
  site: ["site", "infusion set"],
  cannula: ["cannula"],
  cage: ["CAGE", "site age"],
  suspend: ["suspend"],
  alarm: ["alarm"],
  bluetooth: ["Bluetooth", "disconnected"],
  compression: ["compression"],
  hypnogram: ["hypnogram", "stages"],
  rem: ["REM", "deep sleep"],
  a1c: ["A1c", "HbA1c"],
  lab: ["lab"],
  weight: ["weight", "body fat"],
  alert: ["alert"],
  snooze: ["snooze"],
  dnd: ["DND", "do not disturb", "mute"],
  roc: ["rate of change", "ROC"],
  trend: ["trend", "arrow"],
  noise: ["noise"],
  consumable: ["consumable", "supplies"],
  hrv: ["HRV", "heart rate variability"],
  spo2: ["SpO2", "blood oxygen", "oxygen"],
  breathing: ["breathing", "respiration"],
  score: ["score"],
  episode: ["episode", "hypo", "hyper"],
};

/**
 * The other words a search finds a signal by, keyed by the catalogue's search keys: abbreviations and the names
 * people use, in the current locale. A search also matches the English words, which `ENGLISH_SEARCH_TERMS` keeps
 * in step with these; "blood sugar" is only there, since the interface names that term glucose.
 */
export function searchTermWords(): Record<string, string[]> {
  return {
    // @wc-include
    bg: ["BG"],
    bloodSugar: [],
    // @wc-include
    sensor: ["sensor"],
    // @wc-include
    cgm: ["CGM"],
    // @wc-include
    targetRange: ["target"],
    // @wc-include
    percentile: ["percentile"],
    // @wc-include
    meter: ["meter"],
    // @wc-include
    fingerstick: ["fingerstick"],
    // @wc-include
    tir: ["TIR"],
    // @wc-include
    tbr: ["TBR"],
    // @wc-include
    tar: ["TAR"],
    // @wc-include
    range: ["range"],
    // @wc-include
    low: ["low", "hypo"],
    // @wc-include
    high: ["high", "hyper"],
    // @wc-include
    average: ["average"],
    // @wc-include
    iob: ["IOB"],
    // @wc-include
    insulin: ["insulin"],
    // @wc-include
    bolus: ["bolus"],
    // @wc-include
    dose: ["dose"],
    // @wc-include
    rate: ["rate"],
    // @wc-include
    tempBasal: ["temp basal"],
    // @wc-include
    profile: ["profile"],
    // @wc-include
    mdi: ["MDI"],
    // @wc-include
    pen: ["pen"],
    // @wc-include
    tdd: ["TDD"],
    // @wc-include
    cob: ["COB"],
    // @wc-include
    meal: ["meal"],
    // @wc-include
    food: ["food"],
    // @wc-include
    loop: ["loop"],
    // @wc-include
    automation: ["automation", "closed loop"],
    // @wc-include
    pump: ["pump"],
    // @wc-include
    pod: ["pod"],
    // @wc-include
    battery: ["battery"],
    // @wc-include
    phone: ["phone", "uploader"],
    // @wc-include
    night: ["night"],
    // @wc-include
    bed: ["bed"],
    // @wc-include
    hr: ["HR"],
    // @wc-include
    pulse: ["pulse"],
    // @wc-include
    exercise: ["exercise"],
    // @wc-include
    walking: ["walking"],
    // @wc-include
    isf: ["ISF"],
    // @wc-include
    sensitivity: ["sensitivity"],
    // @wc-include
    icr: ["ICR", "carb ratio"],
    // @wc-include
    override: ["override"],
    // @wc-include
    illness: ["illness"],
    // @wc-include
    gaps: ["gaps", "missing data"],
    // @wc-include
    sage: ["SAGE", "sensor age"],
    // @wc-include
    site: ["site", "infusion set"],
    // @wc-include
    cannula: ["cannula"],
    // @wc-include
    cage: ["CAGE", "site age"],
    // @wc-include
    suspend: ["suspend"],
    // @wc-include
    alarm: ["alarm"],
    // @wc-include
    bluetooth: ["Bluetooth", "disconnected"],
    // @wc-include
    compression: ["compression"],
    // @wc-include
    hypnogram: ["hypnogram", "stages"],
    // @wc-include
    rem: ["REM", "deep sleep"],
    // @wc-include
    a1c: ["A1c", "HbA1c"],
    // @wc-include
    lab: ["lab"],
    // @wc-include
    weight: ["weight", "body fat"],
    // @wc-include
    alert: ["alert"],
    // @wc-include
    snooze: ["snooze"],
    // @wc-include
    dnd: ["DND", "do not disturb", "mute"],
    // @wc-include
    roc: ["rate of change", "ROC"],
    // @wc-include
    trend: ["trend", "arrow"],
    // @wc-include
    noise: ["noise"],
    // @wc-include
    consumable: ["consumable", "supplies"],
    // @wc-include
    hrv: ["HRV", "heart rate variability"],
    // @wc-include
    spo2: ["SpO2", "blood oxygen", "oxygen"],
    // @wc-include
    breathing: ["breathing", "respiration"],
    // @wc-include
    score: ["score"],
    // @wc-include
    episode: ["episode", "hypo", "hyper"],
  };
}

/** The words of a signal's search keys. */
export function searchWords(keys: readonly string[], translated: Record<string, string[]> = searchTermWords()): string[] {
  return withEnglishWords(
    keys.flatMap((k) => (Object.hasOwn(translated, k) ? translated[k] : [])),
    keys.flatMap((k) => (Object.hasOwn(ENGLISH_SEARCH_TERMS, k) ? ENGLISH_SEARCH_TERMS[k] : [])),
  );
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
    // @wc-include
    [EventAnchorKind.Meal]: "meals",
    // @wc-include
    [EventAnchorKind.CorrectionBolus]: "correction boluses",
    // @wc-include
    [EventAnchorKind.AnyBolus]: "boluses",
    // @wc-include
    [EventAnchorKind.UnannouncedRise]: "rises with no carbs",
    // @wc-include
    [EventAnchorKind.LowStart]: "lows",
    // @wc-include
    [EventAnchorKind.HighStart]: "highs",
    // @wc-include
    [EventAnchorKind.LowTreatment]: "carbs taken in a low",
    // @wc-include
    [EventAnchorKind.ExerciseStart]: "exercise",
    // @wc-include
    [EventAnchorKind.SleepStart]: "sleep starts",
    // @wc-include
    [EventAnchorKind.Wake]: "wake-ups",
    // @wc-include
    [EventAnchorKind.SensorStart]: "sensor starts",
    // @wc-include
    [EventAnchorKind.SiteChange]: "site changes",
    // @wc-include
    [EventAnchorKind.OverrideStart]: "overrides",
    // @wc-include
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
    // @wc-include
    "6h": "6 hours",
    // @wc-include
    "24h": "24 hours",
    // @wc-include
    "7d": "7 days",
    // @wc-include
    "14d": "14 days",
    // @wc-include
    "28d": "28 days",
    // @wc-include
    "90d": "90 days",
  };
}

export function rangeLabel(id: string): string {
  return wordOf(rangeLabels(), id);
}

/** The whole days in `hours`: "1 day", or "N days". */
export function daysText(hours: number): string {
  const days = Math.floor(hours / 24);
  if (days === 1) {
    // @wc-include
    return "1 day";
  }
  return `${days} days`;
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
    // @wc-include
    "glucose.read": "glucose",
    // @wc-include
    "treatments.read": "treatments",
    // @wc-include
    "food.read": "food",
    // @wc-include
    "therapy.read": "therapy settings",
    // @wc-include
    "devices.read": "devices",
    // @wc-include
    "heartrate.read": "heart rate",
    // @wc-include
    "sleep.read": "sleep",
    // @wc-include
    "stepcount.read": "steps",
    // @wc-include
    "alerts.read": "alerts",
  };
}

function scopeWord(scope: string): string {
  return wordOf(scopeWords(), scope);
}

/** Why the reader cannot use something that reads `scopes`: its access lacks one of them. */
export function accessReason(scopes: readonly string[]): string {
  const words = scopes.map(scopeWord);
  return words.length > 0
    ? `Your access cannot read ${formatList(words, "disjunction")}, which this needs.`
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
  return {
    // @wc-include
    hour: "by hour of day",
    // @wc-include
    weekday: "by weekday",
  };
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
