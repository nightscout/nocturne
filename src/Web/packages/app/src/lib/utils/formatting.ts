/**
 * Centralized Formatting Utilities
 *
 * Consolidates all formatting functions for the application:
 * - Glucose formatting (with unit conversion)
 * - Treatment formatting
 * - Date formatting
 * - Insulin/carb/percentage display formatting
 */

import { glucoseUnits, type GlucoseUnits } from "$lib/stores/appearance-store.svelte";
import type { Treatment, TreatmentSummary as ApiTreatmentSummary, OverallAverages as ApiOverallAverages } from "$lib/api";

// Re-export for backward compatibility
export type { GlucoseUnits, Treatment };
export type TreatmentSummary = ApiTreatmentSummary;
export type OverallAverages = ApiOverallAverages;

// =============================================================================
// Glucose Conversion & Formatting
// =============================================================================

/** Conversion factor from mg/dL to mmol/L */
const MGDL_TO_MMOL = 18.01559;

/**
 * Convert a glucose value from mg/dL to the specified units
 * @param mgdl - Glucose value in mg/dL
 * @param units - Target units ("mg/dl" or "mmol")
 * @returns Glucose value in the specified units
 */
export function convertToDisplayUnits(mgdl: number, units: GlucoseUnits): number {
  if (units === "mmol") {
    return Math.round((mgdl / MGDL_TO_MMOL) * 10) / 10;
  }
  return Math.round(mgdl);
}

/**
 * Format a glucose value for display with appropriate precision
 * @param mgdl - Glucose value in mg/dL
 * @param units - Display units ("mg/dl" or "mmol")
 * @returns Formatted glucose string
 */
export function formatGlucoseValue(mgdl: number, units: GlucoseUnits) {
  const value = convertToDisplayUnits(mgdl, units);
  if (units === "mmol") {
    return Number(value.toFixed(1));
  }
  return Math.round(value);
}

/**
 * Format a glucose delta value for display
 * @param deltaMgdl - Delta value in mg/dL
 * @param units - Display units ("mg/dl" or "mmol")
 * @param includeSign - Whether to include +/- sign (default: true)
 * @returns Formatted delta string
 */
export function formatGlucoseDelta(
  deltaMgdl: number,
  units: GlucoseUnits,
  includeSign: boolean = true
): string {
  const value = convertToDisplayUnits(deltaMgdl, units);
  const sign = includeSign && value > 0 ? "+" : "";

  if (units === "mmol") {
    return `${sign}${value.toFixed(1)}`;
  }
  return `${sign}${Math.round(value)}`;
}

/**
 * Get the unit label for display
 * @param units - Units type
 * @returns Human-readable unit label
 */
export function getUnitLabel(units: GlucoseUnits): string {
  return units === "mmol" ? "mmol/L" : "mg/dL";
}

/**
 * Format a glucose range for display
 * @param lowMgdl - Low threshold in mg/dL
 * @param highMgdl - High threshold in mg/dL
 * @param units - Display units
 * @returns Formatted range string (e.g., "70-180 mg/dL" or "3.9-10.0 mmol/L")
 */
export function formatGlucoseRange(
  lowMgdl: number,
  highMgdl: number,
  units: GlucoseUnits
): string {
  const low = formatGlucoseValue(lowMgdl, units);
  const high = formatGlucoseValue(highMgdl, units);
  const label = getUnitLabel(units);
  return `${low}-${high} ${label}`;
}

// =============================================================================
// Glucose Convenience Functions (auto-detect units from global preference)
// These are the recommended functions for most use cases.
// =============================================================================

/**
 * Format a glucose value using the global unit preference
 * @param mgdl - Glucose value in mg/dL
 * @returns Formatted glucose string in user's preferred units
 */
export function bg(mgdl: number) {
  return formatGlucoseValue(mgdl, glucoseUnits.current);
}

/**
 * Format a glucose delta using the global unit preference
 * @param deltaMgdl - Delta value in mg/dL
 * @param includeSign - Whether to include +/- sign (default: true)
 * @returns Formatted delta string in user's preferred units
 */
export function bgDelta(deltaMgdl: number, includeSign: boolean = true): string {
  return formatGlucoseDelta(deltaMgdl, glucoseUnits.current, includeSign);
}

/**
 * Get the current unit label from global preference
 * @returns "mg/dL" or "mmol/L" based on user preference
 */
export function bgLabel(): string {
  return getUnitLabel(glucoseUnits.current);
}

/**
 * Format a glucose range using the global unit preference
 * @param lowMgdl - Low threshold in mg/dL
 * @param highMgdl - High threshold in mg/dL
 * @returns Formatted range string in user's preferred units
 */
export function bgRange(lowMgdl: number, highMgdl: number): string {
  return formatGlucoseRange(lowMgdl, highMgdl, glucoseUnits.current);
}

/**
 * Convert a mg/dL value to the user's preferred units
 * @param mgdl - Value in mg/dL
 * @returns Numeric value in user's preferred units
 */
export function bgValue(mgdl: number): number {
  return convertToDisplayUnits(mgdl, glucoseUnits.current);
}

/**
 * Get standard glucose range thresholds in user's preferred units
 * @returns Object with common threshold values
 */
export function bgThresholds(): {
  urgentLow: number;
  low: number;
  targetLow: number;
  targetHigh: number;
  high: number;
  urgentHigh: number;
} {
  const units = glucoseUnits.current;
  return {
    urgentLow: convertToDisplayUnits(54, units),
    low: convertToDisplayUnits(70, units),
    targetLow: convertToDisplayUnits(70, units),
    targetHigh: convertToDisplayUnits(180, units),
    high: convertToDisplayUnits(180, units),
    urgentHigh: convertToDisplayUnits(250, units),
  };
}

// =============================================================================
// Date Formatting
// =============================================================================

/**
 * Formats a date string to display date and time
 * @param dateStr - ISO date string or undefined
 * @returns Formatted date and time string, or fallback
 */
export function formatDateTime(dateStr: string | undefined): string {
  if (!dateStr) return "—";
  const date = new Date(dateStr);
  return date.toLocaleDateString() + " " + date.toLocaleTimeString();
}

/**
 * Formats a date string or Date object to locale string
 * @param date - Date object, ISO date string, or undefined
 * @returns Formatted date and time string, or "N/A"
 */
export function formatDate(date: Date | string | undefined): string {
  if (!date) return "N/A";
  return new Date(date).toLocaleString();
}

/**
 * Formats a date string with detailed formatting options
 * @param dateString - ISO date string or undefined
 * @returns Formatted date and time with full details, or "Unknown"
 */
export function formatDateDetailed(dateString: string | undefined): string {
  if (!dateString) return "Unknown";
  try {
    return new Date(dateString).toLocaleDateString(undefined, {
      year: "numeric",
      month: "long",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  } catch {
    return dateString;
  }
}

/**
 * Formats a date string for use in datetime-local input fields
 * @param dateStr - ISO date string or undefined
 * @returns Date in YYYY-MM-DDTHH:MM format for HTML input
 */
export function formatDateForInput(dateStr: string | undefined): string {
  if (!dateStr) return "";
  const date = new Date(dateStr);
  const year = date.getFullYear();
  const month = (date.getMonth() + 1).toString().padStart(2, "0");
  const day = date.getDate().toString().padStart(2, "0");
  const hours = date.getHours().toString().padStart(2, "0");
  const minutes = date.getMinutes().toString().padStart(2, "0");
  return `${year}-${month}-${day}T${hours}:${minutes}`;
}

/**
 * Formats a date string to compact date and time (short month)
 * @param dateStr - ISO date string or undefined
 * @returns Compact formatted date and time, or "—"
 */
export function formatDateTimeCompact(dateStr: string | undefined): string {
  if (!dateStr) return "—";
  const date = new Date(dateStr);
  return date.toLocaleDateString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

// =============================================================================
// Treatment & Insulin Formatting
// =============================================================================

/**
 * Formats an insulin value for display.
 * @param insulin The insulin value.
 * @returns The formatted insulin string.
 */
export function formatInsulinDisplay(insulin: number | undefined): string {
  if (insulin === undefined || insulin === null) {
    return "N/A";
  }
  return insulin.toFixed(2);
}

/**
 * Formats a carb value for display.
 * @param carbs The carb value.
 * @returns The formatted carb string.
 */
export function formatCarbDisplay(carbs: number | undefined): string {
  if (carbs === undefined || carbs === null) {
    return "N/A";
  }
  return carbs.toFixed(0);
}

/**
 * Formats a percentage value for display.
 * @param value The percentage value.
 * @returns The formatted percentage string.
 */
export function formatPercentageDisplay(value: number | undefined): string {
  if (value === undefined || value === null) {
    return "N/A";
  }
  return value.toFixed(1);
}

/**
 * Formats glucose reading with measurement method
 * @param treatment - Treatment object
 * @returns Formatted glucose string
 */
export function formatGlucose(treatment: Treatment): string {
  if (treatment.glucose && treatment.glucose > 0) {
    let glucoseStr = treatment.glucose.toString();
    if (treatment.glucoseType) {
      glucoseStr += ` (${treatment.glucoseType})`;
    }
    return glucoseStr;
  }
  return "-";
}

/**
 * Formats event type with optional reason
 * @param treatment - Treatment object
 * @returns Formatted event type string
 */
export function formatEventType(treatment: Treatment): string {
  let result = treatment.eventType || "Unknown";

  if (treatment.reason) {
    result += ` - ${treatment.reason}`;
  }

  return result;
}

/**
 * Formats notes and entered by information
 * @param treatment - Treatment object
 * @returns Formatted notes string
 */
export function formatNotes(treatment: Treatment): string {
  const parts: string[] = [];

  if (treatment.notes) {
    parts.push(treatment.notes);
  }

  if (treatment.enteredBy) {
    parts.push(`by ${treatment.enteredBy}`);
  }

  return parts.join(" ");
}
