/**
 * Formatting utilities for the glucose-chart package.
 * Always uses mg/dL — no unit store dependency.
 */

// =============================================================================
// Class Name Utility
// =============================================================================

type ClassValue = string | number | boolean | null | undefined | ClassValue[];

/**
 * Combines class values into a single class string, filtering out falsy values.
 * Lightweight alternative to clsx/twMerge for the standalone package.
 */
export function cn(...inputs: ClassValue[]): string {
  return classNames(inputs, []).join(' ');
}

function classNames(inputs: ClassValue[], found: string[]): string[] {
  for (const input of inputs) {
    if (Array.isArray(input)) classNames(input, found);
    else if (typeof input === 'string' && input.length > 0) found.push(input);
  }
  return found;
}

// =============================================================================
// Glucose Formatting (mg/dL only)
// =============================================================================

/**
 * Format a glucose value for display (mg/dL).
 * @param mgdl - Glucose value in mg/dL
 * @returns Rounded integer as string
 */
export function bg(mgdl: number): string {
  return Math.round(mgdl).toString();
}

/**
 * Format a glucose delta value for display (mg/dL).
 * @param deltaMgdl - Delta value in mg/dL
 * @param includeSign - Whether to include +/- sign (default: true)
 * @returns Formatted delta string
 */
export function bgDelta(deltaMgdl: number, includeSign = true): string {
  const rounded = Math.round(deltaMgdl);
  if (includeSign && rounded > 0) return `+${rounded}`;
  return rounded.toString();
}

/**
 * Get the unit label (always mg/dL for this package).
 * @returns "mg/dL"
 */
export function bgLabel(): string {
  return 'mg/dL';
}

// =============================================================================
// Treatment Formatting
// =============================================================================

/**
 * Formats an insulin value for display.
 * @param insulin - The insulin value.
 * @returns The formatted insulin string, e.g. "1.50U", or "—" if null/undefined.
 */
export function formatInsulinDisplay(insulin: number | undefined): string {
  if (insulin == null) return '—';
  return `${insulin.toFixed(2)}U`;
}

/**
 * Formats a carb value for display.
 * @param carbs - The carb value.
 * @returns The formatted carb string, e.g. "45g", or "—" if null/undefined.
 */
export function formatCarbDisplay(carbs: number | undefined): string {
  if (carbs == null) return '—';
  return `${Math.round(carbs)}g`;
}

// =============================================================================
// Data Source Display
// =============================================================================

/**
 * Maps data source identifiers to human-readable display names.
 * Source identifiers come from DataSources constants on the backend.
 */
const DATA_SOURCE_DISPLAY_NAMES: Record<string, string> = {
  // CGM Connectors
  "dexcom-connector": "Dexcom",
  "libre-connector": "FreeStyle Libre",
  "minimed-connector": "Medtronic",
  "glooko-connector": "Glooko",
  "nightscout-connector": "Nightscout",
  "tidepool-connector": "Tidepool",
  "tconnectsync-connector": "t:connect",
  "mylife-connector": "mylife",

  // Mobile apps / uploaders
  xdrip: "xDrip+",
  spike: "Spike",

  // AID systems
  loop: "Loop",
  openaps: "OpenAPS",
  aaps: "AndroidAPS",
  iaps: "iAPS",
  trio: "Trio",

  // Manual entry
  manual: "Manual Entry",
  careportal: "Careportal",
  "api-client": "API Client",

  // Food
  "myfitnesspal-connector": "MyFitnessPal",

  // Import / migration
  "mongodb-import": "MongoDB Import",
  "csv-import": "CSV Import",
  "tidepool-import": "Tidepool Import",

  // System
  "demo-service": "Demo",
  system: "System",
  websocket: "WebSocket",
};

/**
 * Get a human-readable display name for a data source identifier.
 * Returns null if the source is null/undefined.
 */
export function getDataSourceDisplayName(
  source: string | null | undefined
): string | null {
  if (!source) return null;

  const lower = source.toLowerCase();
  if (lower in DATA_SOURCE_DISPLAY_NAMES) {
    return DATA_SOURCE_DISPLAY_NAMES[lower];
  }

  // Fallback: title-case the raw string, replacing hyphens with spaces
  return source
    .replace(/-/g, " ")
    .replace(/\b\w/g, (c) => c.toUpperCase());
}
