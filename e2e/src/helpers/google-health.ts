import { env } from "./env.ts";
import { eventually } from "./http.ts";
import type { Tenant } from "./tenant.ts";

export const googleTypes = ["steps", "heart-rate", "weight", "sleep"];

export interface GoogleStatus {
  connected: boolean;
  isSyncing: boolean;
  lastSync: string | null;
  errorCode: string | null;
  previewRequired: boolean;
}

export function googleOptions(tenant: Tenant) {
  return {
    clientId: `${tenant.slug}.apps.googleusercontent.com`,
    clientSecret: "e2e-google-secret",
    callbackUrl: `${env.tenantSecureWebUrl(tenant.slug)}/settings/connectors/google-health/callback`,
    dataTypes: googleTypes,
    historyDays: 2,
    importFrom: new Date(Date.now() - 2 * 86_400_000).toISOString().slice(0, 10) + "T00:00:00Z",
    previewOnly: true,
  };
}

export async function googleConsent(authorizationUrl: string): Promise<URL> {
  const url = new URL(authorizationUrl);
  if (url.origin !== "https://accounts.google.com" || url.pathname !== "/o/oauth2/v2/auth")
    throw new Error("Unexpected Google authorization destination");
  const response = await fetch(`${env.mocksUrl}/googlehealth/authorize?${url.searchParams}`);
  if (!response.ok) throw new Error(`Mock consent failed: ${response.status}`);
  const body = await response.json() as { redirectUrl: string };
  return new URL(body.redirectUrl);
}

export async function waitForGoogleSync(tenant: Tenant, previous?: string | null): Promise<GoogleStatus> {
  return eventually(async () => {
    const status = await tenant.api.ok<GoogleStatus>("GET", "/api/v4/google-health");
    return !status.isSyncing && status.lastSync && status.lastSync !== previous && !status.errorCode ? status : false;
  }, { timeoutMs: 45_000, what: "Google Health import completion" });
}

interface Reading { _id: string; bpm?: number; metric?: number; weightKg?: number }
interface Sleep { id: string; durationMs: number }

export async function googleReadings(tenant: Tenant) {
  const heart = await tenant.api.ok<Reading[]>("GET", "/api/v4/HeartRate?count=100");
  const steps = await tenant.api.ok<Reading[]>("GET", "/api/v4/StepCount?count=100");
  const weight = await tenant.api.ok<Reading[]>("GET", "/api/v4/body-weight?count=100");
  const sleep = await tenant.api.ok<{ data: Sleep[] }>("GET", "/api/v4/sleep/sessions");
  return { heart, steps, weight, sleep: sleep.data };
}
