/**
 * Time in Range (TIR) color constants
 *
 * These colors are used consistently across all reports and visualizations
 * for representing different glucose ranges:
 * - Severe Low: <54 mg/dL (<3.0 mmol/L)
 * - Low: 54-69 mg/dL (3.0-3.8 mmol/L)
 * - Target: 70-180 mg/dL (3.9-10.0 mmol/L)
 * - High: 181-250 mg/dL (10.1-13.9 mmol/L)
 * - Severe High: >250 mg/dL (>13.9 mmol/L)
 */

// CSS Custom Properties (for components that can use CSS variables)
export const TIR_COLORS_CSS = {
  severeLow: "var(--glucose-very-low)",
  low: "var(--glucose-low)",
  target: "var(--glucose-in-range)",
  high: "var(--glucose-high)",
  severeHigh: "var(--glucose-very-high)",
} as const;



// Hex Values (alternative format for charts)
export const TIR_COLORS_HEX = {
  severeLow: "#B91C1C", // red-700
  low: "#EF4444",       // red-500
  target: "#22C55E",    // green-500
  high: "#FBBF24",      // yellow-400
  severeHigh: "#D97706", // yellow-600
} as const;


// Type definitions for TypeScript support
export type TIRColorScheme = typeof TIR_COLORS_CSS;
export type TIRRange = keyof TIRColorScheme;

export default TIR_COLORS_CSS;
