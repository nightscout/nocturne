/** Central exports for all constants */

// Re-export meal time constants
export * from './meal-times';

import type { GlycemicThresholds } from "../api";

export const DEFAULT_THRESHOLDS: GlycemicThresholds = {
  low: 55,
  targetBottom: 80,
  targetTop: 140,
  tightTargetBottom: 80,
  tightTargetTop: 120,
  high: 180,
  veryLow: 40,
  veryHigh: 250,
};

export const chartConfig = {
  veryLow: {
    threshold: DEFAULT_THRESHOLDS.veryLow,
    label: "Very Low",
    color: "var(--glucose-very-low)",
  },
  low: {
    threshold: DEFAULT_THRESHOLDS.low,
    label: "Low",
    color: "var(--glucose-low)",
  },
  target: {
    threshold: DEFAULT_THRESHOLDS.targetBottom,
    label: "Target",
    color: "var(--glucose-in-range)",
  },
  high: {
    threshold: DEFAULT_THRESHOLDS.high,
    label: "High",
    color: "var(--glucose-high)",
  },
  veryHigh: {
    threshold: DEFAULT_THRESHOLDS.veryHigh,
    label: "Very High",
    color: "var(--glucose-very-high)",
  },
};
