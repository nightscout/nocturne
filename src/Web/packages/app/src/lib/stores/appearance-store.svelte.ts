/**
 * Appearance Store - Unified store for all appearance settings
 *
 * Uses runed's PersistedState for automatic localStorage persistence with
 * instant reactivity and cross-tab sync. Consolidates:
 * - Color theme (Nocturne vs Trio)
 * - Color scheme (light/dark/system mode via mode-watcher)
 * - Glucose units (mg/dL vs mmol/L)
 * - Time format (12h vs 24h)
 * - Night mode schedule
 */

import { browser } from "$app/environment";
import { PersistedState } from "runed";
import { setMode, mode, userPrefersMode } from "mode-watcher";

// ==========================================
// Type Definitions
// ==========================================

/** Color theme - visual styling (Nocturne custom vs Trio iOS app) */
export type ColorTheme = "nocturne" | "trio";

/** Color scheme - light/dark mode preference */
export type ColorScheme = "system" | "light" | "dark";

/** Glucose units preference */
export type GlucoseUnits = "mg/dl" | "mmol";

/** Time format preference */
export type TimeFormat = "12" | "24";

// ==========================================
// Persisted State Instances
// ==========================================

/**
 * Color theme preference (Nocturne vs Trio)
 * Controls the CSS class applied to the document root
 */
export const colorTheme = new PersistedState<ColorTheme>(
  "nocturne-color-theme",
  "nocturne"
);

/**
 * Blood glucose units preference
 * Automatically persists to localStorage and syncs across tabs
 */
export const glucoseUnits = new PersistedState<GlucoseUnits>(
  "nocturne-glucose-units",
  "mg/dl"
);

/**
 * Time format preference (12-hour or 24-hour)
 */
export const timeFormat = new PersistedState<TimeFormat>(
  "nocturne-time-format",
  "12"
);

/**
 * Night mode schedule toggle
 * When enabled, automatically switches to dark mode at night
 */
export const nightModeSchedule = new PersistedState<boolean>(
  "nocturne-night-mode-schedule",
  false
);

// ==========================================
// Color Theme Management (Nocturne/Trio)
// ==========================================

/**
 * Apply color theme class to document
 */
function applyColorTheme(theme: ColorTheme): void {
  if (!browser) return;

  const root = document.documentElement;
  if (theme === "trio") {
    root.classList.add("trio-theme");
  } else {
    root.classList.remove("trio-theme");
  }
}

/**
 * Set color theme and apply immediately
 */
export function setColorTheme(theme: ColorTheme): void {
  if (colorTheme.current === theme) return;
  colorTheme.current = theme;
  applyColorTheme(theme);
}

/**
 * Get current color theme
 */
export function getColorTheme(): ColorTheme {
  return colorTheme.current;
}

/**
 * Toggle between Nocturne and Trio themes
 */
export function toggleColorTheme(): void {
  setColorTheme(colorTheme.current === "nocturne" ? "trio" : "nocturne");
}

/**
 * Initialize color theme on app load
 */
export function initColorTheme(): void {
  if (!browser) return;
  applyColorTheme(colorTheme.current);
}

// Apply theme on module load in browser
if (browser) {
  // Use setTimeout to ensure DOM is ready
  setTimeout(() => {
    applyColorTheme(colorTheme.current);
  }, 0);
}

// ==========================================
// Color Scheme Management (Light/Dark/System)
// ==========================================

/**
 * Apply color scheme change using mode-watcher
 * This provides instant visual feedback without page reload
 */
export function setColorScheme(value: ColorScheme): void {
  setMode(value);
}

/**
 * Get the current user-preferred mode from mode-watcher
 * Returns "system", "light", or "dark"
 */
export function getColorScheme(): ColorScheme {
  return userPrefersMode.current ?? "system";
}

/**
 * Re-export mode-watcher's reactive mode store
 * This represents the actual current mode ("light" or "dark"),
 * resolved from system preference when set to "system"
 */
export { mode, userPrefersMode };

// ==========================================
// Glucose Units Helpers
// ==========================================

/**
 * Get current glucose units
 */
export function getGlucoseUnits(): GlucoseUnits {
  return glucoseUnits.current;
}

/**
 * Set glucose units
 */
export function setGlucoseUnits(units: GlucoseUnits): void {
  glucoseUnits.current = units;
}

// ==========================================
// Prediction Settings
// ==========================================

/**
 * Prediction time horizon in minutes
 * Controls how far into the future predictions are shown
 */
export const predictionMinutes = new PersistedState<number>(
  "nocturne-prediction-minutes",
  30
);

/**
 * Prediction enabled state
 * Controls whether prediction lines are shown on charts
 */
export const predictionEnabled = new PersistedState<boolean>(
  "nocturne-prediction-enabled",
  true
);

/**
 * Get current prediction minutes
 */
export function getPredictionMinutes(): number {
  return predictionMinutes.current;
}

/**
 * Get current prediction enabled state
 */
export function getPredictionEnabled(): boolean {
  return predictionEnabled.current;
}

/**
 * Set prediction minutes
 */
export function setPredictionMinutes(minutes: number): void {
  predictionMinutes.current = minutes;
}

/**
 * Set prediction enabled state
 */

/**
 * Set prediction enabled state
 */
export function setPredictionEnabled(enabled: boolean): void {
  predictionEnabled.current = enabled;
}

// ==========================================
// Prediction Display Mode
// ==========================================

export type PredictionDisplayMode =
  | "cone"
  | "lines"
  | "main"
  | "iob"
  | "zt"
  | "uam"
  | "cob";

/**
 * Prediction display mode preference
 */
export const predictionDisplayMode = new PersistedState<PredictionDisplayMode>(
  "nocturne-prediction-display-mode",
  "cone"
);

// ==========================================
// Chart Lookback Settings
// ==========================================

export type TimeRangeOption = "2" | "4" | "6" | "12" | "24";

/**
 * Glucose chart lookback hours preference
 */
export const glucoseChartLookback = new PersistedState<TimeRangeOption>(
  "nocturne-glucose-chart-lookback",
  "6"
);

