import {
  DayOfWeek,
  LoopAnalysisSeries,
  SettingsEvidenceBlock,
  SettingsEvidenceConfidence,
  SettingsEvidenceConfidenceReason,
  SettingsEvidenceRule,
  SettingsEvidenceUnavailableReason,
  SettingsEvidenceWithheldReason,
  type BasalEvidenceBlock,
  type CarbRatioEvidenceBlock,
  type IsfEvidenceBlock,
  type LoopAnalysisAggregate,
  type SettingsEvidenceRange,
  type SettingsEvidenceReport,
} from "$lib/api";

const HOUR = 3_600_000;
const C = SettingsEvidenceConfidence;
const R = SettingsEvidenceConfidenceReason;
const U = SettingsEvidenceUnavailableReason;

function spread(centre: number, half: number, n: number): LoopAnalysisAggregate {
  return { p10: centre - 2 * half, p25: centre - half, p50: centre, p75: centre + half, p90: centre + 2 * half, mean: centre, n, sparse: n < 5 };
}

const NAMED: [SettingsEvidenceBlock, number, number][] = [
  [SettingsEvidenceBlock.Overnight, 0, 6],
  [SettingsEvidenceBlock.Morning, 6, 11],
  [SettingsEvidenceBlock.Midday, 11, 17],
  [SettingsEvidenceBlock.Evening, 17, 24],
];

function basal(startHour: number, shift = 0): BasalEvidenceBlock {
  const fasting = startHour < 8;
  const profile = 0.8;
  return {
    startHour,
    endHour: startHour + 2,
    profile,
    n: fasting ? 14 : 0,
    rejections: [
      { rule: SettingsEvidenceRule.CarbsBefore, count: fasting ? 2 : 20 },
      { rule: SettingsEvidenceRule.ManualBolus, count: fasting ? 1 : 3 },
      { rule: SettingsEvidenceRule.OpenLoop, count: 0 },
    ],
    ...(fasting
      ? {
          observed: 1.05 + shift,
          spread: spread(1.05 + shift, 0.1, 14),
          confidence: startHour === 4 ? C.Low : C.Moderate,
          confidenceReasons: startHour === 4 ? [R.FastingUnverifiable, R.MoreInsulinOnBorrowedInput] : [R.FastingUnverifiable],
          towardsMoreInsulin: true,
          meanNetLoopCorrection: 0.25,
          consistency: 0.93,
          glucoseTrend: spread(-1.5, 2, 14),
          days: [
            {
              at: "2026-06-11T16:00:00Z",
              end: "2026-06-11T18:00:00Z",
              date: "2026-06-12",
              time: `0${startHour}:00`.slice(-5),
              value: 1.1,
              profile,
              hours: 2,
              netLoopCorrection: 0.3,
              carriedInsulin: 0.05,
              glucoseChange: -4,
              isf: 50,
            },
          ],
        }
      : { unavailableReason: U.NoneQualified, days: [] }),
  };
}

function isf(block: SettingsEvidenceBlock, start: number, end: number): IsfEvidenceBlock {
  if (block !== SettingsEvidenceBlock.Midday)
    return {
      block, startHour: start, endHour: end, profile: 50, n: 0,
      unavailableReason: block === SettingsEvidenceBlock.Evening ? U.TooFew : U.NoCandidates,
      rejections: [{ rule: SettingsEvidenceRule.NotIsolated, count: block === SettingsEvidenceBlock.Evening ? 6 : 0 }],
      episodes: [],
    };
  return {
    block, startHour: start, endHour: end, profile: 50, n: 8,
    observed: 41, spread: spread(41, 4, 8), confidence: C.Moderate, confidenceReasons: [R.FewEpisodes],
    towardsMoreInsulin: true,
    rejections: [
      { rule: SettingsEvidenceRule.CarbsNear, count: 12 },
      { rule: SettingsEvidenceRule.WentLow, count: 1 },
    ],
    episodes: [
      {
        at: "2026-06-12T04:00:00Z", end: "2026-06-12T08:00:00Z", date: "2026-06-12", time: "14:00", value: 40, profile: 50,
        units: 2, drop: 80, startGlucose: 200, endGlucose: 120, netInsulin: 1.8, iobAtStart: 0.4, iobAtEnd: 0.2,
        residualIob: 0.1, basalExcess: 0.05, insulinActed: 2,
      },
    ],
  };
}

function carbRatio(block: SettingsEvidenceBlock, start: number, end: number, meals: boolean): CarbRatioEvidenceBlock {
  if (!meals || block !== SettingsEvidenceBlock.Morning)
    return {
      block, startHour: start, endHour: end, profile: 10, n: 0,
      unavailableReason: U.NoAnnouncedMeals, rejections: [{ rule: SettingsEvidenceRule.NotIsolated, count: 0 }], episodes: [],
    };
  return {
    block, startHour: start, endHour: end, profile: 10, n: 15,
    observed: 12.5, spread: spread(12.5, 1.5, 15), confidence: C.High, confidenceReasons: [],
    towardsMoreInsulin: false,
    towardsLessInsulin: true,
    rejections: [{ rule: SettingsEvidenceRule.NotIsolated, count: 4 }],
    episodes: [
      {
        at: "2026-06-11T22:00:00Z", end: "2026-06-12T02:00:00Z", date: "2026-06-12", time: "08:00", value: 12, profile: 10,
        grams: 40, startGlucose: 110, endGlucose: 115, netInsulin: 3.2, iobAtStart: 0.1, iobAtEnd: 0, residualIob: 0,
        basalExcess: 0, insulinActed: 3.3, insulinNeeded: 3.4, isf: 50, isfDependent: true,
      },
    ],
  };
}

export function settingsEvidenceRange(options: { meals?: boolean; shift?: number } = {}): SettingsEvidenceRange {
  const meals = options.meals ?? true;
  return {
    from: "2026-06-09T14:00:00Z",
    to: "2026-06-16T14:00:00Z",
    basal: Array.from({ length: 12 }, (_, i) => basal(i * 2, options.shift ?? 0)),
    isf: NAMED.map(([block, start, end]) => isf(block, start, end)),
    carbRatio: NAMED.map(([block, start, end]) => carbRatio(block, start, end, meals)),
  };
}

export function settingsEvidenceResponse(overrides: Partial<SettingsEvidenceReport> = {}): SettingsEvidenceReport {
  return {
    historyClamped: false,
    historyWindowHours: 24,
    primaryRange: { startDate: "2026-06-10", endDate: "2026-06-16", shortened: false },
    appliedFilters: { days: [DayOfWeek.Monday], excludeIllness: true, excludeOpenLoop: true },
    ignoredFilters: [],
    withheldSeries: [],
    unavailableFilters: [],
    profileNames: ["Default"],
    primary: settingsEvidenceRange(),
    rules: {
      basalCarbFreeMs: 4 * HOUR,
      basalManualBolusFreeMs: 5 * HOUR,
      isfCarbFreeBeforeMs: HOUR,
      isfMinimumWindowMs: 3 * HOUR,
      isfWindowMs: 4 * HOUR,
      carbRatioWindowMs: 4 * HOUR,
      lowMgdl: 70,
      startLowMgdl: 70,
      startHighMgdl: 180,
    },
    ...overrides,
  };
}

export const WITHHELD_SETTINGS_EVIDENCE: Partial<SettingsEvidenceReport> = {
  withheldReason: SettingsEvidenceWithheldReason.ReadScopes,
  withheldSeries: [LoopAnalysisSeries.SettingsEvidence],
  primary: undefined,
  comparison: undefined,
};
