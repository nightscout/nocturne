import {
  AidAlgorithm,
  LoopAnalysisSeries,
  PredictionContext,
  PredictionCurve,
  type PredictionAccuracyRange,
  type PredictionAccuracyResponse,
  type PredictionCurveAccuracy,
  type PredictionHorizonAccuracy,
} from "$lib/api";
import { ALL_SCOPES, unavailableFor } from "./daily-profile.test-fixtures";

const HORIZONS = [30, 60, 90, 120];
const ALL_CONTEXTS = [
  PredictionContext.Adjusted,
  PredictionContext.PostMeal,
  PredictionContext.PostCorrection,
  PredictionContext.Fasting,
];
const CONTEXTS: Record<PredictionCurve, PredictionContext[]> = {
  [PredictionCurve.Default]: ALL_CONTEXTS,
  [PredictionCurve.Uam]: ALL_CONTEXTS,
  [PredictionCurve.Iob]: [PredictionContext.PostCorrection, PredictionContext.Fasting],
  [PredictionCurve.ZeroTemp]: [PredictionContext.PostCorrection, PredictionContext.Fasting],
  [PredictionCurve.Cob]: [PredictionContext.PostMeal],
};

export interface PredictionOptions {
  curves?: PredictionCurve[];
  /** Added to every bias, so a comparison range is told apart from the primary. */
  shift?: number;
  sparse?: boolean;
  interrupted?: number;
}

/** A horizon whose bias grows with the horizon: -4, -8, -12 and -16 mg/dL, plus the shift. */
function horizon(minutes: number, options: PredictionOptions): PredictionHorizonAccuracy {
  const bias = -(minutes / 30) * 4 + (options.shift ?? 0);
  const n = options.sparse ? 3 : 400;
  return {
    minutes,
    error: {
      p10: bias - 30,
      p25: bias - 12,
      p50: bias,
      p75: bias + 12,
      p90: bias + 30,
      mean: bias,
      n,
      sparse: options.sparse ?? false,
    },
    biasScale: Math.min(1, Math.abs(bias) / 30),
    mae: 10 + minutes / 6,
    mard: 8 + minutes / 15,
    unmatched: 5,
    excluded: 2,
    interrupted: options.interrupted ?? 0,
    settingsSignal: minutes <= 60,
  };
}

function curve(kind: PredictionCurve, options: PredictionOptions): PredictionCurveAccuracy {
  const horizons = HORIZONS.map((m) => horizon(m, options));
  return {
    curve: kind,
    contexts: CONTEXTS[kind],
    horizons,
    byHour: Array.from({ length: 24 }, (_, hour) => ({
      hour,
      horizons: HORIZONS.map((m) => horizon(m, { ...options, shift: (options.shift ?? 0) + (hour - 12) })),
    })),
    byContext: CONTEXTS[kind].map((context) => ({ context, horizons: HORIZONS.map((m) => horizon(m, options)) })),
  };
}

export function predictionRange(options: PredictionOptions = {}): PredictionAccuracyRange {
  const curves = options.curves ?? [PredictionCurve.Iob, PredictionCurve.ZeroTemp, PredictionCurve.Cob, PredictionCurve.Uam];
  return {
    from: "2026-10-01T00:00:00Z",
    to: "2026-10-15T00:00:00Z",
    predictions: options.sparse ? 12 : 4032,
    algorithms: [curves.includes(PredictionCurve.Default) ? AidAlgorithm.Loop : AidAlgorithm.Trio],
    curves: curves.map((c) => curve(c, options)),
    hiddenCurves: [],
  };
}

const S = LoopAnalysisSeries;

/** The series `LoopAnalysisReadScopeGuard.WithheldPredictions` withholds for these scopes. */
export function predictionsWithheldFor(scopes: readonly string[]): LoopAnalysisSeries[] {
  const glucose = scopes.includes("glucose.read");
  const devices = scopes.includes("devices.read");
  const treatments = scopes.includes("treatments.read");
  return [
    ...(glucose && devices ? [] : [S.PredictionCurves]),
    ...(devices ? [] : [S.PredictionCount]),
    ...(treatments ? [] : [S.PredictionContexts]),
  ];
}

/** Empties exactly what `LoopAnalysisReadScopeGuard.Narrow` empties for these series. */
function narrow(range: PredictionAccuracyRange, withheld: Set<LoopAnalysisSeries>): PredictionAccuracyRange {
  if (withheld.has(S.PredictionCount)) {
    range.predictions = 0;
    range.algorithms = [];
  }
  if (withheld.has(S.PredictionCurves)) range.curves = [];
  if (withheld.has(S.PredictionContexts)) {
    const partial = (c: PredictionCurveAccuracy) => (c.contexts ?? []).length < ALL_CONTEXTS.length;
    // As the guard names them: by the algorithms, whatever curves the snapshots carried.
    range.hiddenCurves = (range.algorithms ?? []).some((a) => a !== AidAlgorithm.Loop)
      ? [PredictionCurve.Iob, PredictionCurve.ZeroTemp, PredictionCurve.Cob]
      : [];
    range.curves = (range.curves ?? []).filter((c) => !partial(c));
    for (const c of range.curves) {
      c.byContext = [];
      for (const h of [...(c.horizons ?? []), ...(c.byHour ?? []).flatMap((x) => x.horizons ?? [])]) h.interrupted = undefined;
    }
  }
  return range;
}

export function predictionResponse(
  options: PredictionOptions & { comparison?: boolean; historyClamped?: boolean; scopes?: readonly string[] } = {}
): PredictionAccuracyResponse {
  const scopes = options.scopes ?? ALL_SCOPES;
  const withheld = predictionsWithheldFor(scopes);
  const set = new Set(withheld);
  return {
    primary: narrow(predictionRange(options), set),
    comparison: options.comparison ? narrow(predictionRange({ ...options, shift: 6 }), set) : undefined,
    primaryRange: { startDate: "2026-10-01", endDate: "2026-10-14", from: "2026-10-01T00:00:00Z", to: "2026-10-15T00:00:00Z", shortened: false },
    comparisonRange: options.comparison
      ? { startDate: "2026-09-17", endDate: "2026-09-30", from: "2026-09-17T00:00:00Z", to: "2026-10-01T00:00:00Z", shortened: false }
      : undefined,
    historyClamped: options.historyClamped ?? false,
    historyWindowHours: 24,
    contextLookbackMs: 3 * 3_600_000,
    ignoredFilters: [],
    withheldSeries: withheld,
    unavailableFilters: unavailableFor(scopes),
  };
}
