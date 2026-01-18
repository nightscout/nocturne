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
 * - Language preference
 */

import { browser } from "$app/environment";
import { PersistedState } from "runed";
import { setMode, mode, userPrefersMode } from "mode-watcher";
import supportedLocales from "../../../../../supportedLocales.json";

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

/** Supported locale type - derived from supportedLocales.json */
export type SupportedLocale = (typeof supportedLocales)[number];

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

export type TimeRangeOption = "2" | "4" | "6" | "12" | "24" | "48";

/**
 * Glucose chart lookback hours preference (display window width)
 * This controls the span of time shown, always ending at "now"
 * Can be a preset value or a custom number from brush selection
 */
export const glucoseChartLookback = new PersistedState<number>(
  "nocturne-glucose-chart-lookback",
  12
);

/**
 * Default fetch range in hours for glucose chart data
 * Always fetches this much data regardless of display range
 */
export const GLUCOSE_CHART_FETCH_HOURS = 48;

// ==========================================
// Language Preference
// ==========================================

/** Re-export supported locales for external use */
export { supportedLocales };

/**
 * Language preference - stored in localStorage and synced to cookie for SSR
 */
export const preferredLanguage = new PersistedState<SupportedLocale>(
  "nocturne-language",
  "en"
);

/** Cookie name for language preference - used by SSR */
export const LANGUAGE_COOKIE_NAME = "nocturne-language";

/**
 * Check if user has explicitly set a language preference
 * Returns true if the localStorage key exists (user has chosen a language)
 */
export function hasLanguagePreference(): boolean {
  if (!browser) return false;
  return localStorage.getItem("nocturne-language") !== null;
}

/**
 * Sync language preference to cookie for server-side access
 */
function syncLanguageCookie(locale: SupportedLocale): void {
  if (!browser) return;
  document.cookie = `${LANGUAGE_COOKIE_NAME}=${locale};path=/;max-age=31536000;SameSite=Lax`;
}

/**
 * Get display name for a language code using Intl.DisplayNames
 * @param code The language code (e.g., "en", "fr")
 * @param displayIn The language to display the name in (defaults to "en")
 * @returns The display name (e.g., "French" or "Français")
 */
export function getLanguageLabel(
  code: SupportedLocale,
  displayIn: SupportedLocale = "en"
): string {
  try {
    const displayNames = new Intl.DisplayNames([displayIn], { type: "language" });
    return displayNames.of(code) ?? code;
  } catch {
    return code;
  }
}

/**
 * Get native language label (language name in its own language)
 * @param code The language code
 * @returns The native label (e.g., "Français" for "fr")
 */
export function getNativeLanguageLabel(code: SupportedLocale): string {
  return getLanguageLabel(code, code);
}

/**
 * Check if a locale is supported
 */
export function isSupportedLocale(locale: string): locale is SupportedLocale {
  return supportedLocales.includes(locale as SupportedLocale);
}

/**
 * Set language preference and sync to cookie
 * Optionally updates the backend user preference via remote function
 * @param locale The locale to set
 * @param updateBackend Optional callback to update backend preference
 */
export async function setLanguage(
  locale: SupportedLocale,
  updateBackend?: (locale: string) => Promise<unknown>
): Promise<void> {
  if (!isSupportedLocale(locale)) {
    console.warn(`Unsupported locale: ${locale}`);
    return;
  }

  preferredLanguage.current = locale;
  syncLanguageCookie(locale);

  // Dynamically load the locale for wuchale
  if (browser) {
    try {
      const { loadLocale } = await import("wuchale/load-utils");
      await loadLocale(locale);
    } catch (error) {
      console.error("Failed to load locale:", error);
    }
  }

  // Update backend preference if callback provided
  if (updateBackend) {
    try {
      await updateBackend(locale);
    } catch (error) {
      console.error("Failed to update backend language preference:", error);
    }
  }
}

/**
 * Get current language preference
 */
export function getLanguage(): SupportedLocale {
  return preferredLanguage.current;
}

// Sync cookie on initial load in browser
if (browser) {
  syncLanguageCookie(preferredLanguage.current);
}
