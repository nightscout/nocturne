/**
 * Appearance Store - Unified store for all appearance settings
 *
 * Per-user display preferences (units, time format, theme, prediction, chart
 * style, widgets) are backed by a `SyncedPref`: it keeps the reactive `.current`
 * API of runed's PersistedState, but additionally
 *  - hydrates from the `nocturne-prefs` cookie / legacy per-key localStorage,
 *  - mirrors every change into one `nocturne-prefs` cookie so a returning device
 *    hydrates synchronously at module load (before first paint), avoiding a wait
 *    on the session/API round-trip, and
 *  - writes through to the backend user-preferences endpoint (injected callback)
 *    so a user's choices follow them across devices and tenants.
 *
 * SSR note: this module's `$state` is shared across requests on the server, so it
 * is NEVER mutated server-side — the cookie is read and applied only in the browser
 * (`if (browser)` at module load). Server-side reads instead resolve from the
 * per-request preference context the root layout provides (see
 * `setPreferencesContext`), so SSR emits the same units/formats hydration will.
 *
 * Language keeps its own dedicated cookie + backend path (used by SSR locale
 * resolution), so it travels in the request context's own slot rather than as a
 * field of the preferences blob.
 */

import { browser } from "$app/environment";
import { getContext, setContext } from "svelte";
import { PersistedState } from "runed";
import { setMode, mode, userPrefersMode } from "mode-watcher";
import supportedLocales from "../../../../../supportedLocales.json";
import { WidgetId } from "../api/generated/nocturne-api-client";
import type { UserDisplayPreferences } from "$lib/api";
import { weekStartName } from "../components/calendar/calendar-date";

// ==========================================
// Type Definitions
// ==========================================

/** Color theme - visual styling */
export type ColorTheme = "nocturne" | "trio" | "aaps" | "classic";

/** Color scheme - light/dark mode preference */
export type ColorScheme = "system" | "light" | "dark";

/** Glucose units preference */
export type GlucoseUnits = "mg/dl" | "mmol";

/** Time format preference */
export type TimeFormat = "12" | "24";

/**
 * Regional format: a BCP-47 tag driving date ordering, month/weekday names and the
 * first day of the week. Empty string follows the display language.
 */
export type RegionFormat = (typeof REGION_FORMATS)[number];

/**
 * Regional formats offered in settings, mirroring the backend allow-list. Labels are
 * derived at render time from `Intl.DisplayNames` plus a sample date, so a new region
 * only needs its tag added here (and to `AllowedRegionFormats` server-side).
 */
export const REGION_FORMATS = [
  "",
  "en-US", "en-GB", "en-AU", "en-CA", "en-IE", "en-NZ", "en-ZA",
  "de-DE", "fr-FR", "es-ES", "it-IT", "nl-NL", "pl-PL", "pt-PT", "pt-BR",
  "sv-SE", "nb-NO", "da-DK", "fi-FI", "cs-CZ", "ru-RU", "ja-JP",
] as const;

/** Supported locale type - derived from supportedLocales.json */
export type SupportedLocale = (typeof supportedLocales)[number];

// ==========================================
// Synced preference infrastructure
// ==========================================

/** Cookie mirroring the per-user synced preferences, written and read client-side for synchronous hydration. */
export const PREFS_COOKIE_NAME = "nocturne-prefs";
const PREFS_COOKIE_MAX_AGE = 31536000; // 1 year

/** Registry of synced prefs by their localStorage key — powers cross-tab sync. */
const syncedRegistry = new Map<string, SyncedPref<unknown>>();

const PREFERENCES_CONTEXT_KEY = Symbol("nocturne-display-preferences");

/**
 * One request's preference sources: the display-preference payloads, highest
 * precedence first and each contributing only the fields it defines, plus the
 * display language, which is stored apart from the blob and so gets its own slot.
 */
export interface RequestPreferences {
  layers: UserDisplayPreferences[];
  language?: SupportedLocale;
}

/**
 * Publish this request's preference sources for the duration of one server render.
 * The store's `$state` is module-scoped and so shared by every concurrent SSR
 * request; reading preferences from component context instead is what keeps one
 * user's units out of another's HTML. Every source mirrors the browser's own
 * resolution order, so SSR output equals the first client render.
 */
export function setPreferencesContext(preferences: () => RequestPreferences): void {
  setContext(PREFERENCES_CONTEXT_KEY, preferences);
}

function requestPreferences(): RequestPreferences | null {
  try {
    return (
      getContext<(() => RequestPreferences) | undefined>(PREFERENCES_CONTEXT_KEY)?.() ??
      null
    );
  } catch {
    // getContext throws outside a component — server loads and module scope have no request.
    return null;
  }
}

/**
 * Backend write-through callback, injected by the app (kept out of this module so
 * the store never imports a server remote directly — mirrors setLanguage's design).
 */
let writeThrough: ((prefs: UserDisplayPreferences) => unknown) | null = null;

/** Register the backend write-through used to persist synced preferences per-user. */
export function registerPreferencesWriteThrough(
  fn: (prefs: UserDisplayPreferences) => unknown
): void {
  writeThrough = fn;
}

let persistTimer: ReturnType<typeof setTimeout> | null = null;

/** Mirror to cookie immediately and debounce the backend write-through. */
function schedulePersist(): void {
  if (!browser) return;
  const prefs = collectPreferences();
  writePrefsCookie(prefs);
  if (persistTimer) clearTimeout(persistTimer);
  persistTimer = setTimeout(() => {
    persistTimer = null;
    writeThrough?.(prefs);
  }, 400);
}

function readInitial<T>(key: string, fallback: T): T {
  if (!browser) return fallback;
  const raw = localStorage.getItem(key);
  if (raw === null) return fallback;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return fallback;
  }
}

function persistLocal<T>(key: string, value: T): void {
  if (!browser) return;
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Ignore quota / disabled-storage errors — the cookie + backend remain sources of truth.
  }
}

/**
 * A per-user preference with the same reactive `.current` surface as runed's
 * PersistedState, so no consumer changes. Writes propagate to localStorage (cache),
 * the shared cookie, and the backend. `hydrate` sets the value without a
 * backend/cookie echo (used when applying server- or cross-tab-sourced values).
 * `read` locates this preference inside a `UserDisplayPreferences` payload, and is
 * the single mapping used for both hydration and server-side resolution.
 */
class SyncedPref<T> {
  private _value = $state<T>(undefined as T);
  private _key: string;
  private _read: (prefs: UserDisplayPreferences) => T | undefined;

  constructor(
    key: string,
    initial: T,
    read: (prefs: UserDisplayPreferences) => T | undefined
  ) {
    this._key = key;
    this._read = read;
    this._value = readInitial(key, initial);
    syncedRegistry.set(key, this as SyncedPref<unknown>);
  }

  get current(): T {
    if (!browser) {
      for (const prefs of requestPreferences()?.layers ?? []) {
        const value = this._read(prefs);
        if (value !== undefined && value !== null) return value;
      }
    }
    return this._value;
  }

  set current(value: T) {
    this._value = value;
    persistLocal(this._key, value);
    schedulePersist();
  }

  /**
   * Apply an externally-sourced value (server/cross-tab) without re-persisting outward.
   * INVARIANT: browser-only. Callers (reconcilePreferences, module-load cookie hydration,
   * the storage listener) are all `if (browser)`-gated; never call this during SSR — the
   * `$state` is shared across requests and would leak one user's value into another's render.
   */
  hydrate(value: T): void {
    this._value = value;
    persistLocal(this._key, value);
  }

  /** Hydrate from a preference payload, leaving the current value alone when unset. */
  hydrateFrom(prefs: UserDisplayPreferences): void {
    const value = this._read(prefs);
    if (value !== undefined && value !== null) this.hydrate(value);
  }
}

// ==========================================
// Persisted State Instances
// ==========================================

/**
 * Color theme preference (Nocturne vs Trio)
 * Controls the CSS class applied to the document root
 */
export const colorTheme = new SyncedPref<ColorTheme>(
  "nocturne-color-theme",
  "nocturne",
  (p) => p.colorTheme as ColorTheme | undefined
);

/**
 * Blood glucose units preference. Per-user: syncs across devices/tenants.
 */
export const glucoseUnits = new SyncedPref<GlucoseUnits>(
  "nocturne-glucose-units",
  "mg/dl",
  (p) => p.glucoseUnits as GlucoseUnits | undefined
);

/**
 * Time format preference (12-hour or 24-hour)
 */
export const timeFormat = new SyncedPref<TimeFormat>(
  "nocturne-time-format",
  "12",
  (p) => p.timeFormat as TimeFormat | undefined
);

/**
 * Regional format preference. Empty (the default) means "follow the display language",
 * which is what a user who never opens this setting gets. Set it to e.g. "en-GB" or
 * "de-DE" for European date ordering and Monday-first calendars while keeping the
 * interface in another language.
 */
export const regionFormat = new SyncedPref<RegionFormat>(
  "nocturne-region-format",
  "",
  (p) => p.regionFormat as RegionFormat | undefined
);

/**
 * Night mode schedule toggle
 * When enabled, automatically switches to dark mode at night
 */
export const nightModeSchedule = new SyncedPref<boolean>(
  "nocturne-night-mode-schedule",
  false,
  (p) => p.nightModeSchedule
);

/**
 * Dashboard top widgets configuration
 * Stores the ordered list of widget IDs displayed in the top widget grid
 */
export const dashboardTopWidgets = new SyncedPref<WidgetId[]>(
  "nocturne-dashboard-top-widgets",
  [WidgetId.BgDelta, WidgetId.TirChart, WidgetId.Tdd],
  (p) => p.dashboardTopWidgets
);

// ==========================================
// Color Theme Management (Nocturne/Trio)
// ==========================================

/** All theme CSS classes that can be applied to the document root */
const THEME_CLASSES = ["trio-theme", "aaps-theme", "classic-theme"] as const;

/**
 * Apply color theme class to document
 */
function applyColorTheme(theme: ColorTheme): void {
  if (!browser) return;

  const root = document.documentElement;
  root.classList.remove(...THEME_CLASSES);

  if (theme === "trio") root.classList.add("trio-theme");
  else if (theme === "aaps") root.classList.add("aaps-theme");
  else if (theme === "classic") root.classList.add("classic-theme");

  // Classic theme uses minimal border radius (2015 utilitarian aesthetic)
  if (theme === "classic") {
    root.style.setProperty("--radius", "0.25rem");
  } else {
    root.style.removeProperty("--radius");
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
export const predictionMinutes = new SyncedPref<number>(
  "nocturne-prediction-minutes",
  30,
  (p) => p.prediction?.minutes
);

/**
 * Prediction enabled state
 * Controls whether prediction lines are shown on charts
 */
export const predictionEnabled = new SyncedPref<boolean>(
  "nocturne-prediction-enabled",
  true,
  (p) => p.prediction?.enabled
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

export type LineColorMode = "single" | "threshold" | "continuous";
export type AreaMode = "off" | "baseline" | "deviation";

/**
 * Prediction display mode preference
 */
export const predictionDisplayMode = new SyncedPref<PredictionDisplayMode>(
  "nocturne-prediction-display-mode",
  "cone",
  (p) => p.prediction?.displayMode as PredictionDisplayMode | undefined
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
export const glucoseChartLookback = new SyncedPref<number>(
  "nocturne-glucose-chart-lookback",
  12,
  (p) => p.chart?.lookback
);

/**
 * Default fetch range in hours for glucose chart data
 * Always fetches this much data regardless of display range
 */
export const GLUCOSE_CHART_FETCH_HOURS = 48;

// ==========================================
// Glucose Chart Visual Style
// ==========================================

export const chartLineColorMode = new SyncedPref<LineColorMode>(
  "nocturne-chart-line-color-mode",
  "threshold",
  (p) => p.chart?.lineColorMode as LineColorMode | undefined
);

export const chartLineColor = new SyncedPref<string>(
  "nocturne-chart-line-color",
  "#22c55e",
  (p) => p.chart?.lineColor
);

export const chartPointColorMode = new SyncedPref<LineColorMode>(
  "nocturne-chart-point-color-mode",
  "threshold",
  (p) => p.chart?.pointColorMode as LineColorMode | undefined
);

export const chartPointColor = new SyncedPref<string>(
  "nocturne-chart-point-color",
  "#22c55e",
  (p) => p.chart?.pointColor
);

export const chartShowPoints = new SyncedPref<boolean>(
  "nocturne-chart-show-points",
  true,
  (p) => p.chart?.showPoints
);

export const chartAreaMode = new SyncedPref<AreaMode>(
  "nocturne-chart-area-mode",
  "off",
  (p) => p.chart?.areaMode as AreaMode | undefined
);

export const chartAreaOpacity = new SyncedPref<number>(
  "nocturne-chart-area-opacity",
  0.5,
  (p) => p.chart?.areaOpacity
);

/**
 * Always render chart range/category patterns on screen, not just in print.
 * An accessibility aid for colour-blind and low-vision users — textures
 * distinguish series that otherwise rely on colour alone.
 */
export const chartAlwaysShowPatterns = new SyncedPref<boolean>(
  "nocturne-chart-always-show-patterns",
  false,
  (p) => p.chart?.alwaysShowPatterns
);

// ==========================================
// Collect / apply the synced preference set
// ==========================================

/**
 * Assemble the full per-user preference payload from the current store values.
 * Shape mirrors the backend `UserDisplayPreferences` (nested prediction/chart).
 */
export function collectPreferences(): UserDisplayPreferences {
  return {
    glucoseUnits: glucoseUnits.current,
    timeFormat: timeFormat.current,
    regionFormat: regionFormat.current,
    colorTheme: colorTheme.current,
    nightModeSchedule: nightModeSchedule.current,
    dashboardTopWidgets: dashboardTopWidgets.current,
    prediction: {
      enabled: predictionEnabled.current,
      minutes: predictionMinutes.current,
      displayMode: predictionDisplayMode.current,
    },
    chart: {
      lineColorMode: chartLineColorMode.current,
      lineColor: chartLineColor.current,
      pointColorMode: chartPointColorMode.current,
      pointColor: chartPointColor.current,
      showPoints: chartShowPoints.current,
      areaMode: chartAreaMode.current,
      areaOpacity: chartAreaOpacity.current,
      alwaysShowPatterns: chartAlwaysShowPatterns.current,
      lookback: glucoseChartLookback.current,
    },
  } as UserDisplayPreferences;
}

/**
 * Apply a server- (or cookie-) sourced preference payload to the store without
 * echoing back to the backend. Only fields present in `prefs` are applied.
 * Optionally refreshes the cookie mirror so it matches the applied state.
 */
export function applyPreferences(
  prefs: UserDisplayPreferences | null | undefined,
  options: { refreshCookie?: boolean } = {}
): void {
  if (!prefs) return;

  for (const pref of syncedRegistry.values()) pref.hydrateFrom(prefs);

  if (browser) applyColorTheme(colorTheme.current);
  if (options.refreshCookie) writePrefsCookie(collectPreferences());
}

/** True when a server preference payload carries at least one saved value. */
export function hasStoredPreferences(prefs: UserDisplayPreferences | null | undefined): boolean {
  if (!prefs) return false;
  return Object.values(prefs).some((v) => v !== undefined && v !== null);
}

/** Ensures reconciliation happens once per document load, not on every client navigation. */
let preferencesReconciled = false;

/**
 * Reconcile the store against the authenticated user's server preferences on load.
 * - server has values -> apply them (server wins across devices)
 * - server empty AND this device has customizations -> seed the backend from them once
 * Generalizes the language reconciliation previously inlined in +layout.ts.
 *
 * Runs at most once per document load: `+layout.ts`'s universal `load` re-runs on every
 * client navigation, but `data.user` is derived only from `locals` (no url/params dependency),
 * so it stays frozen at the initial snapshot. Re-applying it on each navigation would revert a
 * preference the user just changed in-session, so we guard with `preferencesReconciled`.
 */
export function reconcilePreferences(serverPrefs: UserDisplayPreferences | null | undefined): void {
  if (!browser || preferencesReconciled) return;
  preferencesReconciled = true;

  if (hasStoredPreferences(serverPrefs)) {
    applyPreferences(serverPrefs, { refreshCookie: true });
  } else if (hasAnyLocalPreference()) {
    // No server blob yet, but this device carries customizations (existing user or a device
    // that already synced): seed the backend from them once. Gated on local customization so an
    // uncustomized device never seeds all-defaults over another device's real preferences.
    const prefs = collectPreferences();
    writePrefsCookie(prefs);
    writeThrough?.(prefs);
  }
}

/** True if any synced preference has a stored value on this device (legacy localStorage / prior sync). */
function hasAnyLocalPreference(): boolean {
  if (!browser) return false;
  for (const key of syncedRegistry.keys()) {
    if (localStorage.getItem(key) !== null) return true;
  }
  return false;
}

// ==========================================
// Preference cookie helpers
// ==========================================

function writePrefsCookie(prefs: UserDisplayPreferences): void {
  if (!browser) return;
  const value = encodeURIComponent(JSON.stringify(prefs));
  document.cookie = `${PREFS_COOKIE_NAME}=${value};path=/;max-age=${PREFS_COOKIE_MAX_AGE};SameSite=Lax`;
}

/** Decode a raw `nocturne-prefs` cookie value; also used server-side by the root layout load. */
export function parsePrefsCookie(
  value: string | null | undefined
): UserDisplayPreferences | null {
  if (!value) return null;
  try {
    const parsed: unknown = JSON.parse(decodeURIComponent(value));
    return parsed && typeof parsed === "object" ? (parsed as UserDisplayPreferences) : null;
  } catch {
    return null;
  }
}

function readPrefsCookie(): UserDisplayPreferences | null {
  if (!browser) return null;
  const match = document.cookie
    .split("; ")
    .find((row) => row.startsWith(`${PREFS_COOKIE_NAME}=`));
  if (!match) return null;
  return parsePrefsCookie(match.slice(PREFS_COOKIE_NAME.length + 1));
}

// Hydrate synchronously from the cookie on load (before first paint) so a known
// device renders the user's units/theme immediately; server reconciliation
// (via reconcilePreferences in +layout) then confirms/corrects.
if (browser) {
  applyPreferences(readPrefsCookie());

  // Cross-tab sync: mirror another tab's change into this tab's store.
  window.addEventListener("storage", (event) => {
    if (!event.key || event.newValue === null) return;
    const pref = syncedRegistry.get(event.key);
    if (!pref) return;
    try {
      pref.hydrate(JSON.parse(event.newValue));
    } catch {
      // Ignore malformed cross-tab payloads.
    }
    if (event.key === "nocturne-color-theme") applyColorTheme(colorTheme.current);
  });
}

// ==========================================
// Language Preference
// ==========================================

/** Re-export supported locales for external use */
export { supportedLocales };

const DEFAULT_LANGUAGE: SupportedLocale = "en";

/**
 * Language preference - stored in localStorage and synced to cookie for SSR.
 * Kept out of the display-preferences blob because server-side locale resolution
 * reads its own cookie + subject column; server-side reads therefore take the
 * request context's language slot rather than a `UserDisplayPreferences` field.
 */
class LanguagePref {
  private _persisted = new PersistedState<SupportedLocale>(
    "nocturne-language",
    DEFAULT_LANGUAGE
  );

  get current(): SupportedLocale {
    if (!browser) return requestPreferences()?.language ?? DEFAULT_LANGUAGE;
    return this._persisted.current;
  }

  set current(locale: SupportedLocale) {
    this._persisted.current = locale;
  }
}

export const preferredLanguage = new LanguagePref();

/** Cookie name for language preference - used by SSR */
export const LANGUAGE_COOKIE_NAME = "nocturne-language";

/**
 * The language the browser will settle on, from the same sources in the same order:
 * a saved subject preference outranks the cookie that mirrors localStorage. SSR
 * output only matches hydration while this order matches the client's.
 */
export function resolveLanguage(
  ...candidates: (string | null | undefined)[]
): SupportedLocale {
  for (const candidate of candidates) {
    if (candidate && isSupportedLocale(candidate)) return candidate;
  }
  return DEFAULT_LANGUAGE;
}

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

/** A date whose day, month and year are all unambiguous, so a sample shows the real ordering. */
const REGION_SAMPLE_DATE = new Date(2026, 11, 31);

/**
 * Label for a regional format: the region's name in the user's own language, then the
 * date it produces and the day its weeks start on. Both samples are spelled out because
 * neither is guessable from a country name — Portugal writes 31/12/2026 but still starts
 * its weeks on Sunday — and nobody picks a calendar style by its BCP-47 tag.
 */
export function regionFormatLabel(tag: RegionFormat): string {
  if (!tag) return "Match my language";

  const region = tag.split("-")[1];
  let name: string = tag;
  try {
    name =
      new Intl.DisplayNames([preferredLanguage.current], { type: "region" }).of(
        region
      ) ?? tag;
  } catch {
    // Unknown region subtag: the tag itself is still a usable label.
  }

  const sample = REGION_SAMPLE_DATE.toLocaleDateString(tag);
  const weekStart = weekStartName(tag, preferredLanguage.current);
  return `${name} — ${sample}, weeks start ${weekStart}`;
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

  // Dynamically load the locale for wuchale. A failure propagates, as it does
  // from the +layout.ts loaders: swallowing it would leave the preference
  // switched with no catalog behind it, which renders every message as ''.
  if (browser) {
    const { loadLocale } = await import("wuchale/load-utils");
    await loadLocale(locale);
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
