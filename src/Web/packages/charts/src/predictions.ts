/** Prediction point with timestamp for charting */
export interface PredictionPoint {
  timestamp: number;
  value: number;
}

/** Prediction curves with timestamps for visualization */
export interface PredictionCurves {
  /** Main prediction curve */
  main: PredictionPoint[];
  /** IOB-only prediction */
  iobOnly: PredictionPoint[];
  /** UAM prediction */
  uam: PredictionPoint[];
  /** COB prediction */
  cob: PredictionPoint[];
  /** Zero-temp prediction */
  zeroTemp: PredictionPoint[];
}

/** Transformed prediction response for the frontend */
export interface PredictionData {
  timestamp: Date;
  currentBg: number;
  delta: number;
  eventualBg: number;
  iob: number;
  cob: number;
  sensitivityRatio: number | null;
  intervalMinutes: number;
  curves: PredictionCurves;
}

/** How forecasts draw: a cone over every curve, every curve as a line, or one curve. */
export type PredictionDisplayMode = "cone" | "lines" | "main" | "iob" | "zt" | "uam" | "cob";
