import type { TenantAlertSettingsResponse } from "$api-clients";

/**
 * Whether Do Not Disturb is suppressing non-critical rules right now.
 *
 * Only the manual mute is knowable from the response. `dndScheduleEnabled` means
 * a recurring window is *configured*, not that it is currently in effect: the
 * backend evaluates the window against the patient's timezone
 * (TenantAlertSettingsSnapshot.Resolve) and the response carries no "active now"
 * field. Treating the flag as "on" told a user with 22:00-07:00 quiet hours that
 * DND was on at noon.
 */
export function isDndActiveNow(
  s: TenantAlertSettingsResponse | null | undefined
): boolean {
  return s?.dndManualActive ?? false;
}

/**
 * Whether a recurring quiet-hours window is configured. Says nothing about
 * whether it is in effect at this moment.
 */
export function isDndScheduleConfigured(
  s: TenantAlertSettingsResponse | null | undefined
): boolean {
  return s?.dndScheduleEnabled ?? false;
}
