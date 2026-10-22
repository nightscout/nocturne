import type { TenantAlertSettingsResponse } from "$api-clients";

type Settings = TenantAlertSettingsResponse | null | undefined;

/**
 * Whether a manual mute is in force. Only this one can be turned off from the
 * UI.
 */
export function isDndManualActive(s: Settings): boolean {
  return s?.dndManualActive ?? false;
}

/**
 * Whether the recurring quiet-hours window is in effect now, as the backend
 * evaluated it against the patient's timezone.
 */
export function isDndScheduleActiveNow(s: Settings): boolean {
  return s?.dndScheduleActiveNow ?? false;
}

/**
 * Whether Do Not Disturb is on now, by either path. It silences every rule that
 * is neither critical nor allowed through it.
 */
export function isDndInEffect(s: Settings): boolean {
  return isDndManualActive(s) || isDndScheduleActiveNow(s);
}

/** Whether a recurring quiet-hours window is configured, in effect or not. */
export function isDndScheduleConfigured(s: Settings): boolean {
  return s?.dndScheduleEnabled ?? false;
}
