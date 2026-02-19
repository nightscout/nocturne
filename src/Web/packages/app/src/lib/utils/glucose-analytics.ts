
export interface GlucoseAnalytics {
  average: number;
  stdDev: number;
  min: number;
  max: number;
  tir: number; // Time in Range (percentage)
}

export interface ExtendedAnalysisConfig {
  veryLow: number;
  low: number;
  target: number;
  high: number;
  veryHigh: number;
}

export const DEFAULT_CONFIG: ExtendedAnalysisConfig = {
  veryLow: 54,
  low: 70,
  target: 180,
  high: 250,
  veryHigh: 400,
};

export function getGlucoseColor(value: number, thresholds: any) {
  if (!thresholds) {
    thresholds = {
      bgHigh: 180,
      bgTargetTop: 140,
      bgTargetBottom: 80,
      bgLow: 55,
    };
  }

  if (value >= thresholds.bgHigh) return "bg-red-500 text-white";
  if (value <= thresholds.bgLow) return "bg-red-500 text-white";
  if (value > thresholds.bgTargetTop) return "bg-orange-500 text-white";
  if (value < thresholds.bgTargetBottom) return "bg-yellow-500 text-black";
  return "bg-green-500 text-white";
}
