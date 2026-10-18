import { describe, expect, it } from "vitest";
import { env } from "../helpers/env.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";
import { CALIBRATION_MGDL, EVERSENSE_PASSWORD, EVERSENSE_USER_ID } from "../../mocks/vendors/eversense.ts";

// Mirrors e2e/mocks/vendors/eversense.ts: a reading every five minutes, and a manual sync without
// a resume point reads the last four hours, as the Eversense NOW graph does.
const READINGS_IN_4H = 4 * 12;

interface VendorRequest {
  path: string;
  query: Record<string, string>;
  body: string;
}

interface SyncResult {
  success: boolean;
  itemsSynced: Record<string, number>;
  errors: string[];
}

interface SensorGlucose {
  mills: number;
  mgdl: number;
  direction?: string | null;
  dataSource: string;
}

interface Page<T> {
  data: T[];
}

async function vendorRequests(): Promise<VendorRequest[]> {
  return (await fetch(`${env.mocksUrl}/eversense/__requests`)).json() as Promise<VendorRequest[]>;
}

async function connect(tenant: Tenant, server: "US" | "OUS", password = EVERSENSE_PASSWORD) {
  await tenant.api.ok("PUT", "/api/v4/connectors/config/eversense", {
    username: `${tenant.slug}@example.com`,
    server,
    isActive: true,
  });
  await tenant.api.ok("PUT", "/api/v4/connectors/config/eversense/secrets", { password });
}

async function readings(tenant: Tenant): Promise<SensorGlucose[]> {
  const res = await tenant.api.get<Page<SensorGlucose>>("/api/v4/glucose/sensor?limit=500");
  return res.body.data;
}

describe("Eversense connector", () => {
  it("imports the glucose history with the latest reading and its trend", async () => {
    const tenant = await seedTenant();
    await connect(tenant, "US");
    const before = (await vendorRequests()).length;

    const result = await tenant.api.ok<SyncResult>("POST", "/api/v4/services/connectors/eversense/sync", {});

    expect(result.success).toBe(true);
    expect(result.errors).toEqual([]);
    expect(result.itemsSynced.Glucose).toBeGreaterThanOrEqual(READINGS_IN_4H);

    const stored = await readings(tenant);
    expect(stored.length).toBeGreaterThanOrEqual(READINGS_IN_4H);
    expect(new Set(stored.map((r) => r.mills)).size).toBe(stored.length);
    expect(stored.every((r) => r.dataSource === "eversense-connector")).toBe(true);
    // Newest first: only the latest reading comes with a trend; the calibration row is not glucose.
    expect(stored[0]!.direction).toBe("Flat");
    expect(stored.some((r) => r.mgdl === CALIBRATION_MGDL)).toBe(false);

    const calls = (await vendorRequests()).slice(before);
    const history = calls.filter((c) => c.path.endsWith("/api/care/GetFollowingUserSensorGlucose"));
    expect(history.length).toBeGreaterThan(0);
    expect(history.every((c) => c.path.startsWith("/usapialpha.eversensedms.com/"))).toBe(true);
    expect(history.every((c) => c.query["UserID"] === String(EVERSENSE_USER_ID))).toBe(true);
  });

  it("does not duplicate readings on a second sync", async () => {
    const tenant = await seedTenant();
    await connect(tenant, "US");
    await tenant.api.ok("POST", "/api/v4/services/connectors/eversense/sync", {});
    const first = await readings(tenant);

    await tenant.api.ok("POST", "/api/v4/services/connectors/eversense/sync", {});
    const second = await readings(tenant);

    expect(new Set(second.map((r) => r.mills)).size).toBe(second.length);
    expect(second.length).toBeLessThanOrEqual(first.length + 1);
  });

  it("signs in and reads from the outside-US hosts", async () => {
    const tenant = await seedTenant();
    await connect(tenant, "OUS");
    const before = (await vendorRequests()).length;

    const result = await tenant.api.ok<SyncResult>("POST", "/api/v4/services/connectors/eversense/sync", {});

    expect(result.success).toBe(true);
    const calls = (await vendorRequests()).slice(before);
    expect(calls.some((c) => c.path === "/ousiamapialpha.eversensedms.com/connect/token")).toBe(true);
    const data = calls.filter((c) => c.path.includes("/api/care/"));
    expect(data.length).toBeGreaterThan(0);
    expect(data.every((c) => c.path.startsWith("/ousalphaapiservices.eversensedms.com/"))).toBe(true);
  });

  it("reports a refused password as a failed sync and stores nothing", async () => {
    const tenant = await seedTenant();
    await connect(tenant, "US", "not-the-password");

    const res = await tenant.api.post<SyncResult>("/api/v4/services/connectors/eversense/sync", {});

    expect(res.status === 200 ? res.body.success : false).toBe(false);
    expect(await readings(tenant)).toEqual([]);

    const tokenCalls = (await vendorRequests()).filter((c) =>
      c.path === "/usiamapi.eversensedms.com/connect/token" &&
      new URLSearchParams(c.body).get("username") === `${tenant.slug}@example.com`);
    expect(tokenCalls).toHaveLength(1);
  });
});
