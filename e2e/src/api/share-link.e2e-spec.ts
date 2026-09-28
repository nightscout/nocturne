import { beforeAll, describe, expect, it } from "vitest";
import { postEntries, sgvSeries } from "../helpers/data.ts";
import { ApiClient, eventually } from "../helpers/http.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface ShareLink {
  enabled: boolean;
  url: string | null;
  fullHistory: boolean;
  scopes: string[];
}

interface V1Entry {
  date: number;
  sgv: number;
}

const HOUR = 60 * 60 * 1000;

describe("public share link", () => {
  let tenant: Tenant;
  let viewer: ApiClient;
  const recent = sgvSeries({ count: 3, device: "e2e-share" });
  const old = sgvSeries({ count: 3, end: Date.now() - 30 * HOUR, device: "e2e-share-old" });

  const entriesSeen = async () => {
    const res = await viewer.get<V1Entry[]>("/api/v1/entries.json?count=50");
    return res.status === 200 ? res.body.map((e) => e.date) : [];
  };

  beforeAll(async () => {
    tenant = await seedTenant();
    await postEntries(tenant.api, [...recent, ...old]);
    const link = await tenant.api.ok<ShareLink>("POST", "/api/v4/share/rotate");
    expect(link.enabled).toBe(true);
    viewer = new ApiClient({ host: new URL(link.url!).host });
  });

  it("is off until the owner enables it", async () => {
    const fresh = await seedTenant();
    const link = await fresh.api.ok<ShareLink>("GET", "/api/v4/share");
    expect(link.enabled).toBe(false);
  });

  it("serves the granted category anonymously", async () => {
    const seen = await eventually(async () => {
      const dates = await entriesSeen();
      return dates.length > 0 ? dates : undefined;
    }, { what: "glucose on the share host" });
    expect(seen).toEqual(expect.arrayContaining(recent.map((e) => e.date)));
  });

  it("refuses categories it was not granted", async () => {
    const link = await tenant.api.ok<ShareLink>("GET", "/api/v4/share");
    expect(link.scopes).toEqual(["glucose.read"]);
    expect((await viewer.get("/api/v1/treatments.json?count=5")).status).toBe(403);
    expect((await viewer.get("/api/v4/insulin/boluses?limit=5")).status).toBe(403);
  });

  it("refuses writes", async () => {
    const res = await viewer.post("/api/v1/entries", sgvSeries({ count: 1, device: "anonymous-writer" }));
    expect([401, 403]).toContain(res.status);
  });

  it("limits history to the last 24 hours by default", async () => {
    const link = await tenant.api.ok<ShareLink>("GET", "/api/v4/share");
    expect(link.fullHistory).toBe(false);
    const seen = await entriesSeen();
    expect(seen).toEqual(expect.arrayContaining(recent.map((e) => e.date)));
    expect(seen.some((d) => old.some((o) => o.date === d))).toBe(false);

    const owner = await tenant.api.get<V1Entry[]>("/api/v1/entries.json?count=50");
    expect(owner.body.map((e) => e.date)).toEqual(expect.arrayContaining(old.map((e) => e.date)));
  });

  it("shows full history once the owner allows it", async () => {
    await tenant.api.ok("PUT", "/api/v4/share/full-history", { fullHistory: true });
    const seen = await eventually(async () => {
      const dates = await entriesSeen();
      return old.every((o) => dates.includes(o.date)) ? dates : undefined;
    }, { what: "older glucose on the share host" });
    expect(seen).toEqual(expect.arrayContaining(old.map((e) => e.date)));
  });

  it("widens to treatments when the owner grants them", async () => {
    await tenant.api.ok("PUT", "/api/v4/share/scopes", { scopes: ["glucose.read", "treatments.read"] });
    await eventually(async () => (await viewer.get("/api/v1/treatments.json?count=5")).status === 200, {
      what: "treatments on the share host",
      timeoutMs: 75_000,
      intervalMs: 1_000,
    });
  });

  it("stops working when disabled, and a rotated link replaces the old one", async () => {
    const rotated = await tenant.api.ok<ShareLink>("POST", "/api/v4/share/rotate");
    const replaced = viewer;
    viewer = new ApiClient({ host: new URL(rotated.url!).host });
    expect(new URL(rotated.url!).host).not.toBe(replaced.host);
    expect((await replaced.get("/api/v1/entries.json?count=1")).status).toBeGreaterThanOrEqual(400);
    expect((await viewer.get("/api/v1/entries.json?count=1")).status).toBe(200);

    await tenant.api.ok("DELETE", "/api/v4/share");
    expect((await viewer.get("/api/v1/entries.json?count=1")).status).toBeGreaterThanOrEqual(400);
  });
});
