import { beforeAll, describe, expect, it } from "vitest";
import { postEntries, sgvSeries } from "../helpers/data.ts";
import { eventually } from "../helpers/http.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface SensorGlucose {
  id: string;
  mgdl: number;
  mmol: number;
  mills: number;
  timestamp: string;
  device: string;
}

interface Page<T> {
  data: T[];
}

describe("v4 sensor glucose", () => {
  let tenant: Tenant;
  const series = sgvSeries({ count: 6, device: "e2e-v4", valueAt: (i) => 100 + i * 10 });

  beforeAll(async () => {
    tenant = await seedTenant();
    await postEntries(tenant.api, series);
  });

  it("projects v1 uploads into the v4 canonical stream, newest first", async () => {
    const page = await eventually(async () => {
      const res = await tenant.api.get<Page<SensorGlucose>>("/api/v4/glucose/sensor?limit=6");
      return res.status === 200 && res.body.data.length === 6 ? res.body : undefined;
    }, { what: "six v4 sensor readings" });

    expect(page.data.map((g) => g.mills)).toEqual(series.map((e) => e.date));
    expect(page.data.map((g) => g.mgdl)).toEqual(series.map((e) => e.sgv));
    expect(page.data.every((g) => g.device === "e2e-v4")).toBe(true);
  });

  it("carries the mmol/L conversion", async () => {
    const res = await tenant.api.get<Page<SensorGlucose>>("/api/v4/glucose/sensor?limit=1");
    const [latest] = res.body.data;
    expect(latest!.mmol).toBeCloseTo(latest!.mgdl / 18.0182, 1);
  });

  it("serves a single reading by id", async () => {
    const res = await tenant.api.get<Page<SensorGlucose>>("/api/v4/glucose/sensor?limit=1");
    const [latest] = res.body.data;
    const one = await tenant.api.get<SensorGlucose>(`/api/v4/glucose/sensor/${latest!.id}`);
    expect(one.status).toBe(200);
    expect(one.body.mgdl).toBe(latest!.mgdl);
  });

  it("bounds a query by time", async () => {
    const from = new Date(series[2]!.date).toISOString();
    const res = await tenant.api.get<Page<SensorGlucose>>(`/api/v4/glucose/sensor?from=${encodeURIComponent(from)}&limit=50`);
    expect(res.status).toBe(200);
    expect(res.body.data.length).toBeGreaterThan(0);
    expect(res.body.data.every((g) => g.mills >= series[2]!.date)).toBe(true);
  });
});
