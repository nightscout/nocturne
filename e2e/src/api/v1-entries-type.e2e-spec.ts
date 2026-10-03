import { beforeAll, describe, expect, it } from "vitest";
import { postEntries, sgvSeries, type SgvEntry } from "../helpers/data.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface V1Entry {
  _id: string;
  type: string;
  date: number;
  sgv?: number;
  mbg?: number;
}

describe("legacy v1 GET /entries/{type} honours count and find", () => {
  let tenant: Tenant;
  let series: SgvEntry[];

  beforeAll(async () => {
    tenant = await seedTenant();
    series = sgvSeries({ count: 30, device: "e2e-type-route", valueAt: (i) => 100 + i });
    await postEntries(tenant.api, series);
    await postEntries(
      tenant.api,
      [0, 1, 2].map((i) => {
        const date = series[i]!.date - 60_000;
        return { type: "mbg", mbg: 140 + i, date, dateString: new Date(date).toISOString(), device: "e2e-meter" };
      }),
    );
  });

  it("returns ten sgv entries when count is absent", async () => {
    const res = await tenant.api.get<V1Entry[]>("/api/v1/entries/sgv.json");
    expect(res.status).toBe(200);
    expect(res.body.map((e) => e.date)).toEqual(series.slice(0, 10).map((e) => e.date));
  });

  it("returns count sgv entries, newest first", async () => {
    const res = await tenant.api.get<V1Entry[]>("/api/v1/entries/sgv.json?count=25");
    expect(res.status).toBe(200);
    expect(res.body.map((e) => e.date)).toEqual(series.slice(0, 25).map((e) => e.date));
    expect(res.body.every((e) => e.type === "sgv")).toBe(true);
  });

  it("narrows to a find[date] range", async () => {
    const from = series[14]!.date;
    const to = series[5]!.date;
    const res = await tenant.api.get<V1Entry[]>(
      `/api/v1/entries/sgv.json?count=100&find[date][$gte]=${from}&find[date][$lte]=${to}`,
    );
    expect(res.status).toBe(200);
    expect(res.body.map((e) => e.date)).toEqual(series.slice(5, 15).map((e) => e.date));
  });

  it("applies a find[sgv] filter together with count", async () => {
    const res = await tenant.api.get<V1Entry[]>("/api/v1/entries/sgv.json?count=3&find[sgv][$gte]=120");
    expect(res.status).toBe(200);
    expect(res.body.map((e) => e.sgv)).toEqual([120, 121, 122]);
  });

  it("lets the path type replace find[type]", async () => {
    const res = await tenant.api.get<V1Entry[]>("/api/v1/entries/mbg.json?count=50&find[type]=sgv");
    expect(res.status).toBe(200);
    expect(res.body.map((e) => e.mbg)).toEqual([140, 141, 142]);
    expect(res.body.every((e) => e.type === "mbg")).toBe(true);
  });
});
