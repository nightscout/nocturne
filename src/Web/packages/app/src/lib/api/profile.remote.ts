/**
 * Custom profile helpers and utility functions
 *
 * Note: V4 API uses therapy settings (profiles.generated.remote.ts). This file
 * contains only utility functions for working with profile data.
 */

/**
 * Local Profile type (not exported from generated client) Represents a
 * Nightscout-compatible profile with therapy settings
 */
export interface Profile {
  _id?: string;
  defaultProfile?: string;
  startDate?: string;
  mills?: number;
  created_at?: string;
  units?: string;
  store?: Record<string, any>;
  enteredBy?: string;
  loopSettings?: any;
  isExternallyManaged?: boolean;
  icon?: string;
  timezone?: string;
}

/** Profile store names (basal, carbratio, sens, target) for displaying */
export type ProfileStoreName = keyof NonNullable<Profile["store"]>;

/** Get profile store names from a profile */
export function getProfileStoreNames(profile: Profile): string[] {
  if (!profile.store) return [];
  return Object.keys(profile.store).map(String);
}

/** Helper to format a time value entry for display */
export function formatTimeValue(
  time: string | undefined,
  value: number | undefined
): string {
  if (!time || value === undefined) return "–";
  return `${time}: ${value}`;
}

/** Convert profile time values to chart-friendly format */
export function timeValuesToChartData(
  timeValues: Array<{ time?: string; value?: number }> | undefined,
  label: string
): Array<{ time: string; value: number; label: string }> {
  if (!timeValues) return [];

  return timeValues
    .filter((tv) => tv.time !== undefined && tv.value !== undefined)
    .map((tv) => ({
      time: tv.time!,
      value: tv.value!,
      label,
    }));
}
