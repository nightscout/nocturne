import { beforeAll, describe, expect, it } from "vitest";
import { env } from "../helpers/env.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";
import { NIGHTSCOUT_API_SECRET_HEADER } from "../../mocks/vendors/nightscout.ts";

// Mirrors e2e/mocks/vendors/nightscout.ts, which serves 48 hours of five-minute readings.
const FAKE_SECRET = "e2e-fake-nightscout-secret";
const FAKE_DEVICE = "e2e-fake-nightscout";
const READINGS_IN_48H = 48 * 12;

interface VendorRequest {
  path: string;
  query: Record<string, string>;
  headers: { "api-secret": string };
}

interface SyncResult {
  success: boolean;
  itemsSynced: Record<string, number>;
  errors: string[];
}

interface V1Entry {
  device: string;
  date: number;
}

interface V1Treatment {
  eventType: string;
  insulin?: number;
  carbs?: number;
}

async function vendorRequests(): Promise<VendorRequest[]> {
  return (await fetch(`${env.mocksUrl}/nightscout/__requests`)).json() as Promise<VendorRequest[]>;
}

describe("Nightscout connector", () => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
    await tenant.api.ok("PUT", "/api/v4/connectors/config/nightscout", {
      url: `${env.mocksUrlFromApi}/nightscout`,
      isActive: true,
    });
    await tenant.api.ok("PUT", "/api/v4/connectors/config/nightscout/secrets", { apiSecret: FAKE_SECRET });
  });

  it("stores the secret without echoing it back", async () => {
    const config = await tenant.api.get<{ configuration: Record<string, unknown> }>("/api/v4/connectors/config/nightscout");
    expect(config.status).toBe(200);
    expect(JSON.stringify(config.body)).not.toContain(FAKE_SECRET);
  });

  it("syncs glucose and treatments from the source", async () => {
    const before = (await vendorRequests()).length;
    const result = await tenant.api.ok<SyncResult>("POST", "/api/v4/services/connectors/nightscout/sync", {});

    expect(result.success).toBe(true);
    expect(result.errors).toEqual([]);
    // One reading can straddle the window edge depending on when the sync lands on the grid.
    expect(result.itemsSynced.Glucose).toBeGreaterThanOrEqual(READINGS_IN_48H - 1);
    expect(result.itemsSynced.Boluses).toBeGreaterThanOrEqual(1);
    expect(result.itemsSynced.CarbIntake).toBeGreaterThanOrEqual(1);

    const calls = (await vendorRequests()).slice(before);
    const authenticated = calls.filter((c) => c.path !== "/api/v1/status.json");
    expect(authenticated.length).toBeGreaterThan(0);
    expect(authenticated.every((c) => c.headers["api-secret"] === NIGHTSCOUT_API_SECRET_HEADER)).toBe(true);
    expect(calls.some((c) => c.path === "/api/v1/entries.json" && c.query["find[date][$lte]"])).toBe(true);
    expect(calls.some((c) => c.path === "/api/v1/treatments.json")).toBe(true);
  });

  it("makes the synced data readable through the API", async () => {
    const entries = await tenant.api.get<V1Entry[]>("/api/v1/entries.json?count=1000");
    const synced = entries.body.filter((e) => e.device === FAKE_DEVICE);
    expect(synced.length).toBeGreaterThanOrEqual(READINGS_IN_48H - 1);

    const treatments = await tenant.api.get<V1Treatment[]>("/api/v1/treatments.json?count=50");
    const meal = treatments.body.find((t) => t.eventType === "Meal Bolus");
    expect(meal).toMatchObject({ insulin: 3.5, carbs: 45 });
  });

  it("does not duplicate records on a second sync", async () => {
    await tenant.api.ok("POST", "/api/v4/services/connectors/nightscout/sync", {});
    const entries = await tenant.api.get<V1Entry[]>("/api/v1/entries.json?count=2000");
    const synced = entries.body.filter((e) => e.device === FAKE_DEVICE);
    expect(new Set(synced.map((e) => e.date)).size).toBe(synced.length);
    expect(synced.length).toBeLessThanOrEqual(READINGS_IN_48H + 1);
  });

  it("reports a wrong secret as a failed sync instead of storing anything", async () => {
    const other = await seedTenant();
    await other.api.ok("PUT", "/api/v4/connectors/config/nightscout", { url: `${env.mocksUrlFromApi}/nightscout`, isActive: true });
    await other.api.ok("PUT", "/api/v4/connectors/config/nightscout/secrets", { apiSecret: "not-the-secret" });

    const res = await other.api.post<SyncResult>("/api/v4/services/connectors/nightscout/sync", {});
    expect(res.status === 200 ? res.body.success : false).toBe(false);
    const entries = await other.api.get<V1Entry[]>("/api/v1/entries.json?count=5");
    expect(entries.body).toEqual([]);
  });
});
