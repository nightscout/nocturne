import { beforeAll, describe, expect, it } from "vitest";
import { minutesAgo, postEntries, postTreatments, sgvSeries } from "../helpers/data.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface V1Entry {
  _id: string;
  sgv: number;
  date: number;
  type: string;
  device: string;
}

interface V1Treatment {
  _id: string;
  eventType: string;
  insulin?: number;
  carbs?: number;
  notes?: string;
  created_at: string;
}

describe("legacy v1 API round-trip", () => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  it("returns uploaded entries newest first, with their values intact", async () => {
    const series = sgvSeries({ count: 12, device: "e2e-v1" });
    await postEntries(tenant.api, series);

    const res = await tenant.api.get<V1Entry[]>("/api/v1/entries.json?count=12");
    expect(res.status).toBe(200);
    expect(res.body).toHaveLength(12);
    expect(res.body.map((e) => e.date)).toEqual(series.map((e) => e.date));
    expect(res.body.map((e) => e.sgv)).toEqual(series.map((e) => e.sgv));
    expect(res.body.every((e) => e.type === "sgv" && e.device === "e2e-v1")).toBe(true);
  });

  it("does not duplicate an entry uploaded twice", async () => {
    const [one] = sgvSeries({ count: 1, end: Date.now() - 3 * 60 * 60 * 1000, device: "e2e-dupe" });
    await postEntries(tenant.api, [one!]);
    await postEntries(tenant.api, [one!]);

    const res = await tenant.api.get<V1Entry[]>(`/api/v1/entries.json?find[date][$eq]=${one!.date}`);
    expect(res.status).toBe(200);
    expect(res.body.filter((e) => e.date === one!.date)).toHaveLength(1);
  });

  it("filters entries by date range", async () => {
    const from = Date.now() - 30 * 60 * 1000;
    const res = await tenant.api.get<V1Entry[]>(`/api/v1/entries.json?count=100&find[date][$gte]=${from}`);
    expect(res.status).toBe(200);
    expect(res.body.length).toBeGreaterThan(0);
    expect(res.body.every((e) => e.date >= from)).toBe(true);
  });

  it("round-trips treatments", async () => {
    const meal = { eventType: "Meal Bolus", created_at: minutesAgo(40), insulin: 4.25, carbs: 52, enteredBy: "e2e" };
    const note = { eventType: "Note", created_at: minutesAgo(20), notes: "e2e synthetic note", enteredBy: "e2e" };
    await postTreatments(tenant.api, [meal, note]);

    const res = await tenant.api.get<V1Treatment[]>("/api/v1/treatments.json?count=10");
    expect(res.status).toBe(200);
    const byType = new Map(res.body.map((t) => [t.eventType, t]));
    expect(byType.get("Meal Bolus")).toMatchObject({ insulin: 4.25, carbs: 52 });
    expect(byType.get("Note")?.notes).toBe("e2e synthetic note");
    expect(Date.parse(byType.get("Meal Bolus")!.created_at)).toBe(Date.parse(meal.created_at));
  });

  it("deletes a treatment by id", async () => {
    await postTreatments(tenant.api, [{ eventType: "Carb Correction", created_at: minutesAgo(90), carbs: 11, enteredBy: "e2e" }]);
    const before = await tenant.api.get<V1Treatment[]>("/api/v1/treatments.json?count=50");
    const target = before.body.find((t) => t.eventType === "Carb Correction" && t.carbs === 11);
    expect(target).toBeDefined();

    const del = await tenant.api.delete(`/api/v1/treatments/${target!._id}`);
    expect(del.status).toBeLessThan(300);

    const after = await tenant.api.get<V1Treatment[]>("/api/v1/treatments.json?count=50");
    expect(after.body.find((t) => t._id === target!._id)).toBeUndefined();
  });
});
