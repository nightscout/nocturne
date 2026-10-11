import { describe, expect, it } from "vitest";
import { env } from "../helpers/env.ts";
import { googleConsent, googleOptions, googleReadings, waitForGoogleSync } from "../helpers/google-health.ts";
import { seedTenant } from "../helpers/tenant.ts";

describe("Google Health OAuth security and sleep deletion", () => {
  it("invalidates a pending OAuth callback on purge and allows a fresh sign-in", async () => {
    const tenant = await seedTenant();
    await tenant.api.ok("PUT", "/api/v4/google-health/options", googleOptions(tenant));
    const pending = await tenant.api.ok<{ url: string }>("POST", "/api/v4/google-health/start");
    const callback = await googleConsent(pending.url);
    await tenant.api.ok("DELETE", "/api/v4/google-health/readings");
    const rejected = await tenant.api.post<{ detail: string }>("/api/v4/google-health/complete", {
      code: callback.searchParams.get("code"), state: callback.searchParams.get("state"),
    });
    expect(rejected.status).toBe(400);
    expect(rejected.body.detail).toBe("expired_signin");
    expect((await tenant.api.ok<{ connected: boolean }>("GET", "/api/v4/google-health")).connected).toBe(false);
    expect(await googleReadings(tenant)).toEqual({ heart: [], steps: [], weight: [], sleep: [] });

    const fresh = await tenant.api.ok<{ url: string }>("POST", "/api/v4/google-health/start");
    const freshCallback = await googleConsent(fresh.url);
    expect(freshCallback.searchParams.get("state")).not.toBe(callback.searchParams.get("state"));
    await tenant.api.ok("POST", "/api/v4/google-health/complete", {
      code: freshCallback.searchParams.get("code"), state: freshCallback.searchParams.get("state"),
    });
    expect((await tenant.api.ok<{ connected: boolean }>("GET", "/api/v4/google-health")).connected).toBe(true);
  });

  it("rejects wrong state and replayed authorization callbacks", async () => {
    const tenant = await seedTenant();
    await tenant.api.ok("PUT", "/api/v4/google-health/options", googleOptions(tenant));
    const start = await tenant.api.ok<{ url: string }>("POST", "/api/v4/google-health/start");
    const callback = await googleConsent(start.url);
    const payload = { code: callback.searchParams.get("code"), state: callback.searchParams.get("state") };
    const wrong = await tenant.api.post<{ detail: string }>("/api/v4/google-health/complete", { ...payload, state: "wrong-state" });
    expect(wrong.status).toBe(400);
    expect(wrong.body.detail).toBe("expired_signin");
    expect((await tenant.api.ok<{ connected: boolean }>("GET", "/api/v4/google-health")).connected).toBe(false);
    expect(await googleReadings(tenant)).toEqual({ heart: [], steps: [], weight: [], sleep: [] });
    const other = await seedTenant();
    const stolen = await other.api.post<{ detail: string }>("/api/v4/google-health/complete", payload);
    expect(stolen.status).toBe(400);
    expect(stolen.body.detail).toBe("expired_signin");
    await tenant.api.ok("POST", "/api/v4/google-health/complete", payload);
    const replay = await tenant.api.post<{ detail: string }>("/api/v4/google-health/complete", payload);
    expect(replay.status).toBe(400);
    expect(replay.body.detail).toBe("expired_signin");
  });

  it("imports all pages, refreshes tokens and keeps user-deleted sleep deleted", async () => {
    const tenant = await seedTenant();
    const options = googleOptions(tenant);
    await tenant.api.ok("PUT", "/api/v4/google-health/options", options);
    const start = await tenant.api.ok<{ url: string }>("POST", "/api/v4/google-health/start");
    const callback = await googleConsent(start.url);
    await tenant.api.ok("POST", "/api/v4/google-health/complete", {
      code: callback.searchParams.get("code"), state: callback.searchParams.get("state"),
    });
    await tenant.api.ok("POST", "/api/v4/google-health/preview");
    await tenant.api.ok("PUT", "/api/v4/google-health/options", { ...options, previewOnly: false });
    await tenant.api.ok("POST", "/api/v4/google-health/sync");
    const firstStatus = await waitForGoogleSync(tenant);
    const first = await googleReadings(tenant);
    for (const readings of Object.values(first)) expect(readings).toHaveLength(2);
    await tenant.api.ok("DELETE", `/api/v4/sleep/sessions/${first.sleep[0]!.id}`);
    await tenant.api.ok("POST", "/api/v4/google-health/sync");
    await waitForGoogleSync(tenant, firstStatus.lastSync);
    const repeated = await googleReadings(tenant);
    expect(repeated.sleep.map((row) => row.id)).toEqual([first.sleep[1]!.id]);
    for (const key of ["heart", "steps", "weight"] as const) expect(repeated[key]).toHaveLength(2);
    const calls = await (await fetch(`${env.mocksUrl}/googlehealth/__requests`)).json() as { path: string; body: string; query: Record<string, string> }[];
    const ownTokens = calls.filter((call) => call.path === "/oauth2.googleapis.com/token" &&
      new URLSearchParams(call.body).get("client_id") === options.clientId);
    expect(ownTokens.some((call) => new URLSearchParams(call.body).get("grant_type") === "authorization_code")).toBe(true);
    expect(ownTokens.some((call) => new URLSearchParams(call.body).get("grant_type") === "refresh_token")).toBe(true);
  });
});
