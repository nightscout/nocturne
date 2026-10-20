import { OAuthScope } from "../api/generated/nocturne-api-client";
import { scopeLabels } from "./scope-labels.svelte";

export { OAuthScope } from "../api/generated/nocturne-api-client";

// The device.* scopes gate the alert engine's device-actuation feature.
export const DEVICE_NOTIFY_SCOPE = "device.notify";
export const DEVICE_ACTUATE_SCOPE = "device.actuate";

/** Scopes that grant hardware/device actuation and warrant extra emphasis in consent UI. */
export const SENSITIVE_DEVICE_SCOPES: ReadonlySet<string> = new Set([
  DEVICE_ACTUATE_SCOPE,
]);

/** True for scopes that control device hardware (torch/vibrate/sound/full-screen). */
export function isSensitiveDeviceScope(scope: string): boolean {
  return SENSITIVE_DEVICE_SCOPES.has(scope);
}

export const OAUTH_AVAILABLE_SCOPES: ReadonlyArray<OAuthScope> = Object.values(OAuthScope);

export function getOAuthScopeDescription(scope: OAuthScope): string;
export function getOAuthScopeDescription(scope: string): string;
export function getOAuthScopeDescription(scope: string): string {
  const labels: Readonly<Record<string, string>> = scopeLabels();
  return Object.hasOwn(labels, scope) ? labels[scope] : scope;
}
