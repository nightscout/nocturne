import {
  DayOfWeek,
  IobCobSource,
  LoopAnalysisSeries,
  type DailyProfileDay,
  type DailyProfileRange,
  type DailyProfileResponse,
  type DailyProfileSlot,
  type LoopAnalysisAggregate,
} from "$lib/api";

/** A coverage bitmap, laid out as `DailyProfileDayCoverage` documents, with the given slots set. */
export function bitmapOf(slots: number[]): string {
  const bytes = new Uint8Array(36);
  for (const slot of slots) bytes[slot >> 3] |= 1 << (slot & 7);
  return btoa(String.fromCharCode(...bytes));
}

const WEEK = [
  DayOfWeek.Sunday,
  DayOfWeek.Monday,
  DayOfWeek.Tuesday,
  DayOfWeek.Wednesday,
  DayOfWeek.Thursday,
  DayOfWeek.Friday,
  DayOfWeek.Saturday,
];

const ALL_SLOTS =Array.from({ length: 288 }, (_, i) => i);

function aggregate(centre: number, spread: number, n: number, sparse: boolean): LoopAnalysisAggregate {
  return {
    p10: centre - 2 * spread,
    p25: centre - spread,
    p50: centre,
    p75: centre + spread,
    p90: centre + 2 * spread,
    mean: centre,
    n,
    sparse,
  };
}

export interface RangeOptions {
  days?: number;
  /** Scales every value, so a comparison range is told apart from the primary. */
  scale?: number;
  sparse?: boolean;
}

export function profileRange(options: RangeOptions = {}): DailyProfileRange {
  const { days = 14, scale = 1, sparse = false } = options;
  const n = sparse ? 2 : days;
  const slots: DailyProfileSlot[] = ALL_SLOTS.map((slot) => ({
    slot,
    minuteOfDay: slot * 5,
    glucose: aggregate(120 * scale, 15, n, sparse),
    scheduledBasal: aggregate(0.8, 0, n, sparse),
    deliveredBasal: aggregate(0.7 * scale, 0.2, n, sparse),
    netLoopCorrection: {
      mean: 0.4 * scale,
      automaticBolusMean: 0.5 * scale,
      rate: aggregate(-0.1 * scale, 0.1, n, sparse),
    },
    manualTemp: aggregate(0, 0, n, sparse),
    suspend: aggregate(0, 0, n, sparse),
    inferred: aggregate(0, 0, n, sparse),
    iob: aggregate(1.5 * scale, 0.5, n, sparse),
    iobSource: IobCobSource.Device,
    cob: aggregate(10 * scale, 5, n, sparse),
    cobSource: IobCobSource.Calculated,
    manualBoluses: { meanPerDay: slot === 96 ? 2 * scale : 0, eventsPerDay: slot === 96 ? 0.5 : 0, n, sparse },
    carbs: { meanPerDay: slot === 96 ? 30 * scale : 0, eventsPerDay: slot === 96 ? 0.5 : 0, n, sparse },
    targetLow: 90,
    targetHigh: 150,
  }));

  const dayList: DailyProfileDay[] = Array.from({ length: n }, (_, i) => {
    const date = `2026-10-${String(i + 1).padStart(2, "0")}`;
    return {
      date,
      dayOfWeek: WEEK[(i + 4) % 7],
      start: `${date}T00:00:00Z`,
      complete: true,
      netLoopCorrection: 9.6 * scale,
      totalInsulin: 30,
      deliveredBasal: 14,
      automaticBolus: 10,
      manualBolus: 6,
      coverage: {
        analysed: bitmapOf(ALL_SLOTS),
        glucose: bitmapOf(ALL_SLOTS),
        scheduledBasal: bitmapOf(ALL_SLOTS),
        deliveredBasal: bitmapOf(ALL_SLOTS),
        netLoopCorrection: bitmapOf(ALL_SLOTS),
        iobCob: bitmapOf(ALL_SLOTS),
      },
    };
  });

  return {
    from: "2026-10-01T00:00:00Z",
    to: "2026-10-15T00:00:00Z",
    slotMinutes: 5,
    slots,
    hourlyNetLoopCorrection: Array.from({ length: 24 }, (_, hour) => ({ hour, mean: 0.4 * scale, n, sparse })),
    days: dayList,
    summary: {
      daysWithData: n,
      deliveredToScheduledRatio: 0.88,
      netLoopCorrectionPerDay: 9.6 * scale,
      netLoopCorrectionTddPercent: 32,
      netLoopCorrectionDays: n,
      insulin: {
        total: aggregate(30, 4, n, sparse),
        deliveredBasal: 14,
        automaticBolus: 10,
        manualBolus: 6,
        bolus: 16,
        deliveredBasalPercent: 46.7,
        bolusPercent: 53.3,
        automaticBolusPercent: 33.3,
        manualBolusPercent: 20,
      },
    },
  };
}

const S = LoopAnalysisSeries;

/** The series `LoopAnalysisReadScopeGuard.WithheldDailyProfile` withholds for these scopes. */
export function withheldFor(scopes: readonly string[]): LoopAnalysisSeries[] {
  const glucose = scopes.includes("glucose.read");
  const treatments = scopes.includes("treatments.read");
  const therapy = scopes.includes("therapy.read");
  return [
    ...(glucose ? [] : [S.Glucose]),
    ...(treatments
      ? []
      : [
          S.DeliveredBasal,
          S.NetLoopCorrection,
          S.NetLoopCorrectionComponents,
          S.HourlyNetLoopCorrection,
          S.Iob,
          S.Cob,
          S.ManualBoluses,
          S.Carbs,
          S.InsulinSummary,
          S.NetLoopCorrectionSummary,
        ]),
    ...(therapy && treatments ? [] : [S.ScheduledBasal, S.Target, S.DeliveredToScheduledRatio]),
    ...(therapy ? [] : [S.ProfileNames]),
  ];
}

/** Empties exactly what `LoopAnalysisReadScopeGuard.Narrow` empties for these series. */
function narrow(range: DailyProfileRange, withheld: Set<LoopAnalysisSeries>): DailyProfileRange {
  const hide = (series: LoopAnalysisSeries) => withheld.has(series);
  for (const slot of range.slots ?? []) {
    if (hide(S.Glucose)) slot.glucose = undefined;
    if (hide(S.ScheduledBasal)) slot.scheduledBasal = undefined;
    if (hide(S.DeliveredBasal)) slot.deliveredBasal = undefined;
    if (hide(S.NetLoopCorrection)) slot.netLoopCorrection = undefined;
    if (hide(S.NetLoopCorrectionComponents)) {
      slot.manualTemp = undefined;
      slot.suspend = undefined;
      slot.inferred = undefined;
    }
    if (hide(S.Iob)) {
      slot.iob = undefined;
      slot.iobSource = undefined;
    }
    if (hide(S.Cob)) {
      slot.cob = undefined;
      slot.cobSource = undefined;
    }
    if (hide(S.ManualBoluses)) slot.manualBoluses = undefined;
    if (hide(S.Carbs)) slot.carbs = undefined;
    if (hide(S.Target)) {
      slot.targetLow = undefined;
      slot.targetHigh = undefined;
    }
  }
  for (const day of range.days ?? []) {
    const coverage = day.coverage ?? {};
    if (hide(S.Glucose)) coverage.glucose = undefined;
    if (hide(S.ScheduledBasal)) coverage.scheduledBasal = undefined;
    if (hide(S.DeliveredBasal)) coverage.deliveredBasal = undefined;
    if (hide(S.NetLoopCorrection)) {
      coverage.netLoopCorrection = undefined;
      day.netLoopCorrection = undefined;
    }
    if (hide(S.Iob) || hide(S.Cob)) coverage.iobCob = undefined;
    if (hide(S.InsulinSummary)) {
      day.totalInsulin = undefined;
      day.deliveredBasal = undefined;
      day.automaticBolus = undefined;
      day.manualBolus = undefined;
    }
  }
  const summary = range.summary ?? {};
  if (hide(S.HourlyNetLoopCorrection)) range.hourlyNetLoopCorrection = [];
  if (hide(S.NetLoopCorrectionSummary)) {
    summary.netLoopCorrectionPerDay = undefined;
    summary.netLoopCorrectionTddPercent = undefined;
    summary.netLoopCorrectionDays = 0;
  }
  if (hide(S.InsulinSummary)) summary.insulin = undefined;
  if (hide(S.DeliveredToScheduledRatio)) summary.deliveredToScheduledRatio = undefined;
  return range;
}

/** The filters `LoopAnalysisReadScopeGuard.UnavailableFilters` reports for these scopes. */
export function unavailableFor(scopes: readonly string[]) {
  const rules: [string, string][] = [
    ["profileName", "therapy.read"],
    ["excludeOverrides", "treatments.read"],
    ["excludeTemporaryTargets", "treatments.read"],
    ["excludeExercise", "treatments.read"],
    ["excludeIllness", "treatments.read"],
    ["excludeOpenLoop", "devices.read"],
  ];
  return rules
    .filter(([, scope]) => !scopes.includes(scope))
    .map(([filter, requiredScope]) => ({ filter, requiredScope }));
}

export const ALL_SCOPES = ["glucose.read", "treatments.read", "devices.read", "therapy.read", "reports.read"];

export function profileResponse(
  options: RangeOptions & { comparison?: boolean; historyClamped?: boolean; scopes?: readonly string[] } = {}
): DailyProfileResponse {
  const withheld = withheldFor(options.scopes ?? ALL_SCOPES);
  const set = new Set(withheld);
  return {
    primary: narrow(profileRange(options), set),
    comparison: options.comparison ? narrow(profileRange({ ...options, scale: 0.5 }), set) : undefined,
    primaryRange: { startDate: "2026-10-01", endDate: "2026-10-14", from: "2026-10-01T00:00:00Z", to: "2026-10-15T00:00:00Z", shortened: false },
    comparisonRange: options.comparison
      ? { startDate: "2026-09-17", endDate: "2026-09-30", from: "2026-09-17T00:00:00Z", to: "2026-10-01T00:00:00Z", shortened: false }
      : undefined,
    appliedFilters: {
      days: [...WEEK],
      excludeOverrides: false,
      excludeTemporaryTargets: false,
      excludeExercise: false,
      excludeIllness: true,
      excludeOpenLoop: true,
    },
    historyClamped: options.historyClamped ?? false,
    historyWindowHours: 24,
    ignoredFilters: [],
    withheldSeries: withheld,
    unavailableFilters: unavailableFor(options.scopes ?? ALL_SCOPES),
    profileNames: set.has(S.ProfileNames) ? [] : ["Default"],
  };
}
