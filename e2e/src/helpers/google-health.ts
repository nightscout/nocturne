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

/** The instance client e2e/docker-compose.yml configures; every tenant connects through it. */
export const googleClientId = "e2e.apps.googleusercontent.com";

/** Where Google sends every tenant back: the apex, relayed to the tenant by the API. */
export const googleRedirectUri = `https://nocturne.localhost:${env.webTlsPort}/api/v4/google-health/oauth/callback`;

export function googleOptions() {
  return {
    dataTypes: googleTypes,
    historyDays: 2,
    importFrom: new Date(Date.now() - 2 * 86_400_000).toISOString().slice(0, 10) + "T00:00:00Z",
    previewOnly: true,
  };
}

/** Consents as the fake Google account `account` names, as a tenant's owner would pick it. */
export async function googleConsent(authorizationUrl: string, account: string): Promise<URL> {
  const url = new URL(authorizationUrl);
  if (url.origin !== "https://accounts.google.com" || url.pathname !== "/o/oauth2/v2/auth")
    throw new Error("Unexpected Google authorization destination");
  url.searchParams.set("mock_account", account);
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

interface Reading { _id: string; mills: number; bpm?: number; metric?: number; weightKg?: number }
interface Sleep { id: string; startTime: string; endTime: string; durationMs: number }

export async function googleReadings(tenant: Tenant) {
  const heart = await tenant.api.ok<Reading[]>("GET", "/api/v4/HeartRate?count=100");
  const steps = await tenant.api.ok<Reading[]>("GET", "/api/v4/StepCount?count=100");
  const weight = await tenant.api.ok<Reading[]>("GET", "/api/v4/body-weight?count=100");
  const sleep = await tenant.api.ok<{ data: Sleep[] }>("GET", "/api/v4/sleep/sessions");
  return { heart, steps, weight, sleep: sleep.data };
}

// Upserts can refresh storage metadata; compare the stored health records themselves.
export function googleRecordSnapshot(readings: Awaited<ReturnType<typeof googleReadings>>) {
  return {
    heart: readings.heart.map(({ _id, mills, bpm }) => ({ _id, mills, bpm })).sort((a, b) => a._id.localeCompare(b._id)),
    steps: readings.steps.map(({ _id, mills, metric }) => ({ _id, mills, metric })).sort((a, b) => a._id.localeCompare(b._id)),
    weight: readings.weight.map(({ _id, mills, weightKg }) => ({ _id, mills, weightKg })).sort((a, b) => a._id.localeCompare(b._id)),
    sleep: readings.sleep.map(({ id, startTime, endTime, durationMs }) => ({ id, startTime, endTime, durationMs })).sort((a, b) => a.id.localeCompare(b.id)),
  };
}
