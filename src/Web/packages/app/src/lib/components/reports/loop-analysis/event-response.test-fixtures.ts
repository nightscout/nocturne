import {
  EventAnchorKind,
  EventResponseKind,
  LoopAnalysisSeries,
  type EventCohortResult,
  type EventMetricsSummary,
  type EventResponseEvent,
  type EventResponseRange,
  type EventResponseReport,
  type EventResponseSlot,
  type LoopAnalysisAggregate,
} from "#lib/api/index.js";

const aggregate = (p50: number, n: number, sparse = false): LoopAnalysisAggregate => ({
  p10: p50 - 2,
  p25: p50 - 1,
  p50,
  p75: p50 + 1,
  p90: p50 + 2,
  mean: p50,
  n,
  sparse,
});

/** Minutes from the event to each of the 72 slots of a -60 to +300 window. */
/** The limits the API serves, as a fixture states them. */
export const EVENT_LIMITS = {
  maxCohorts: 3,
  maxGrams: 1000,
  minutesBeforeChoices: [0, 30, 60, 90, 120],
  minutesAfterChoices: [120, 180, 240, 300, 360, 420, 480],
};

export const SLOT_MINUTES = Array.from({ length: 72 }, (_, slot) => slot * 5 - 60);

/** A cohort's 72 slots, glucose rising 1 mg/dL a slot after the event. */
function cohort(number: number, events: number, offset = 0): EventCohortResult {
  const slots: EventResponseSlot[] = SLOT_MINUTES.map((minute, slot) => {
    const after = minute >= 0;
    return {
      glucoseChange: aggregate(after ? slot - 12 + offset : 0, events, events < 5),
      glucose: aggregate(120 + (after ? slot - 12 : 0), events, events < 5),
      netLoopCorrection: aggregate(0.5, events),
      netInsulinSinceAnchor: after ? aggregate(1 + slot * 0.05, events) : undefined,
      netLoopCorrectionSinceAnchor: after ? aggregate(slot * 0.02, events) : undefined,
      iob: aggregate(2.5, events),
      cob: aggregate(20, events),
    };
  });
  return {
    cohort: number,
    response: EventResponseKind.Rise,
    anchors: events + 2,
    notIsolated: 1,
    missingAnchorGlucose: 1,
    events,
    gramsPerUnitNotIsolated: number === 1 ? 2 : 0,
    gramsPerUnitNoNetInsulin: number === 1 ? 1 : 0,
    gramsPerUnitUncovered: number === 1 ? 3 : 0,
    slots,
    metrics: {
      startGlucose: aggregate(110, events),
      peakRise: aggregate(65, events),
      minutesToPeak: aggregate(55, events),
      minutesBackToStart: aggregate(150, Math.max(0, events - 4)),
      gramsPerUnit: number === 1 ? aggregate(9.5, events) : undefined,
      netInsulin: aggregate(4.2, events),
      netInsulinFirst60: aggregate(1.1, events),
      iobAtAnchor: aggregate(3, events),
    },
  };
}

/** Every slot covered but the first two, as base64 of the 72-slot bitmap. */
const MOSTLY_COVERED = btoa(String.fromCharCode(0xfc, ...Array<number>(8).fill(0xff)));

const event = (
  date: string,
  time: string,
  anchor: EventAnchorKind,
  cohorts: number[],
  peakRise: number,
  grams?: number
): EventResponseEvent => ({
  at: `${date}T${time}:00Z`,
  date,
  time,
  anchor,
  response: EventResponseKind.Rise,
  grams,
  cohorts,
  glucoseCoverage: MOSTLY_COVERED,
  metrics: { startGlucose: 110, peakRise, minutesToPeak: 50, netInsulin: 4, netInsulinFirst60: 1, iobAtAnchor: 3 },
});

/** A cohort of correction boluses: fall figures in place of the rise ones, one of its events below range and one unobserved. */
function corrections(events: number): EventCohortResult {
  return {
    ...cohort(3, events),
    response: EventResponseKind.Fall,
    wentBelowRange: 1,
    wentBelowRangeObserved: events - 1,
    metrics: {
      startGlucose: aggregate(220, events),
      netInsulin: aggregate(2, events),
      iobAtAnchor: aggregate(2.5, events),
      drop: aggregate(90, events),
      nadir: aggregate(115, events),
      minutesToNadir: aggregate(170, events),
      dropPerUnit: aggregate(45, events),
    },
  };
}

const correction = (date: string, time: string, drop: number, wentBelowRange: boolean): EventResponseEvent => ({
  at: `${date}T${time}:00Z`,
  date,
  time,
  anchor: EventAnchorKind.CorrectionBolus,
  response: EventResponseKind.Fall,
  cohorts: [3],
  glucoseCoverage: MOSTLY_COVERED,
  metrics: {
    startGlucose: 220,
    drop,
    nadir: wentBelowRange ? 65 : 120,
    minutesToNadir: 170,
    wentBelowRange,
    netInsulin: 2,
    dropPerUnit: drop / 2,
    iobAtAnchor: 2.5,
  },
});

function range(listEvents: boolean, fall: boolean): EventResponseRange {
  return {
    cohorts: [cohort(1, 12), cohort(2, 3, 5), ...(fall ? [corrections(4)] : [])],
    events: listEvents
      ? [
          event("2026-10-10", "07:30", EventAnchorKind.Meal, [1], 80, 45),
          event("2026-10-11", "12:05", EventAnchorKind.Meal, [1], 40, 30),
          event("2026-10-12", "19:00", EventAnchorKind.UnannouncedRise, [2], 95),
          ...(fall
            ? [correction("2026-10-13", "15:00", 90, false), correction("2026-10-14", "15:00", 150, true)]
            : []),
        ]
      : [],
  };
}

const INSULIN_FIGURES: (keyof EventMetricsSummary)[] = [
  "totalInsulin", "netInsulin", "gramsPerUnit", "dropPerUnit", "netInsulinFirst30", "netInsulinFirst60", "netInsulinFirst120",
];

export function eventResponse(
  options: { comparison?: boolean; withheld?: LoopAnalysisSeries[]; historyClamped?: boolean; fall?: boolean } = {}
): EventResponseReport {
  const withheld = new Set(options.withheld ?? []);
  const report: EventResponseReport = {
    historyClamped: options.historyClamped ?? false,
    historyWindowHours: 24,
    ignoredFilters: [],
    unavailableFilters: [],
    unavailableAnchors: [],
    withheldSeries: [...withheld],
    slotMinutes: SLOT_MINUTES,
    backToStartMgdl: 25,
    limits: EVENT_LIMITS,
    definition: {
      cohorts: [
        { cohort: 1, anchor: EventAnchorKind.Meal, fromMinute: 300, toMinute: 600, minGrams: 30, maxGrams: 80 },
        { cohort: 2, anchor: EventAnchorKind.UnannouncedRise },
        ...(options.fall ? [{ cohort: 3, anchor: EventAnchorKind.CorrectionBolus }] : []),
      ],
      minutesBefore: 60,
      minutesAfter: 300,
      isolatedOnly: false,
      requireCorroboration: false,
    },
    primary: range(true, options.fall ?? false),
    comparison: options.comparison ? range(false, options.fall ?? false) : undefined,
  };

  // As the API's guard narrows: without glucose there is no event at all.
  for (const r of [report.primary, report.comparison]) {
    if (!r) continue;
    if (withheld.has(LoopAnalysisSeries.EventGlucose)) {
      r.cohorts = [];
      r.events = [];
    }
    if (withheld.has(LoopAnalysisSeries.EventList)) r.events = [];
    for (const c of r.cohorts ?? []) {
      for (const s of c.slots ?? []) {
        if (withheld.has(LoopAnalysisSeries.EventNetInsulin))
          s.netLoopCorrection = s.netInsulinSinceAnchor = s.netLoopCorrectionSinceAnchor = undefined;
        if (withheld.has(LoopAnalysisSeries.EventIob)) s.iob = undefined;
        if (withheld.has(LoopAnalysisSeries.EventCob)) s.cob = undefined;
      }
      if (withheld.has(LoopAnalysisSeries.EventNetInsulin)) {
        for (const key of INSULIN_FIGURES) if (c.metrics) c.metrics[key] = undefined;
        c.gramsPerUnitNotIsolated = c.gramsPerUnitNoNetInsulin = c.gramsPerUnitUncovered = 0;
      }
      if (withheld.has(LoopAnalysisSeries.EventIob) && c.metrics) c.metrics.iobAtAnchor = c.metrics.iobAtEnd = undefined;
    }
  }
  return report;
}

export const TREATMENT_SERIES = [
  LoopAnalysisSeries.EventNetInsulin,
  LoopAnalysisSeries.EventIob,
  LoopAnalysisSeries.EventCob,
  LoopAnalysisSeries.EventList,
];
