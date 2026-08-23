// Relative import (not the $lib alias) so this module stays portable when bundled
// by other workspace packages — the portal docs import it via @nocturne/app/constants.
import { OAuthScope } from "../api/generated/nocturne-api-client";

export { OAuthScope } from "../api/generated/nocturne-api-client";

// The device.* scopes gate the alert engine's device-actuation feature. They are
// not part of the generated OAuthScope enum (which covers data-access scopes), so
// they are keyed by their literal string values here.
export const DEVICE_NOTIFY_SCOPE = "device.notify";
export const DEVICE_ACTUATE_SCOPE = "device.actuate";

export const OAUTH_SCOPE_DESCRIPTIONS: Readonly<Record<string, string>> = {
  [OAuthScope.GlucoseRead]: "View glucose readings",
  [OAuthScope.GlucoseReadWrite]: "View, record and remove glucose readings",
  [OAuthScope.TreatmentsRead]: "View treatments",
  [OAuthScope.TreatmentsReadWrite]: "View, record and remove treatments",
  [OAuthScope.DevicesRead]: "View device status",
  [OAuthScope.DevicesReadWrite]: "View, update and remove device status",
  [OAuthScope.TherapyRead]: "View therapy settings",
  [OAuthScope.TherapyReadWrite]: "View, update and remove therapy settings",
  [OAuthScope.AlertsRead]: "View alerts",
  [OAuthScope.AlertsReadWrite]: "Manage alerts",
  [OAuthScope.ReportsRead]: "View reports and analytics",
  [OAuthScope.IdentityRead]: "View basic account info",
  [OAuthScope.SharingReadWrite]: "Manage sharing settings",
  [OAuthScope.HeartRateRead]: "View heart rate data",
  [OAuthScope.HeartRateReadWrite]: "View, record and remove heart rate data",
  [OAuthScope.StepCountRead]: "View step count data",
  [OAuthScope.StepCountReadWrite]: "View, record and remove step count data",
  [OAuthScope.SleepRead]: "View sleep data",
  [OAuthScope.SleepReadWrite]: "View, record and remove sleep data",
  [OAuthScope.FoodRead]: "View food data",
  [OAuthScope.FoodReadWrite]: "View, record and remove food data",
  [OAuthScope.HealthRead]: "View all health data (read-only)",
  [OAuthScope.HealthReadWrite]: "View, update and remove all health data",
  [OAuthScope.FullAccess]: "Full access to all data and settings",
  [DEVICE_NOTIFY_SCOPE]: "Send notifications to this device",
  [DEVICE_ACTUATE_SCOPE]:
    "Use this device's alarms — sound, vibration, flashlight, and full-screen alerts",
} as const;

/** Scopes that grant hardware/device actuation and warrant extra emphasis in consent UI. */
export const SENSITIVE_DEVICE_SCOPES: ReadonlySet<string> = new Set([
  DEVICE_ACTUATE_SCOPE,
]);

/** True for scopes that control device hardware (torch/vibrate/sound/full-screen). */
export function isSensitiveDeviceScope(scope: string): boolean {
  return SENSITIVE_DEVICE_SCOPES.has(scope);
}

export const OAUTH_AVAILABLE_SCOPES = Object.values(OAuthScope) as ReadonlyArray<OAuthScope>;

export function getOAuthScopeDescription(scope: OAuthScope): string;
export function getOAuthScopeDescription(scope: string): string;
export function getOAuthScopeDescription(scope: string): string {
  return (OAUTH_SCOPE_DESCRIPTIONS as Record<string, string>)[scope] ?? scope;
}
