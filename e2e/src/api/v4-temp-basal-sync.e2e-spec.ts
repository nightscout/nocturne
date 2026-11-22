import { randomBytes } from "node:crypto";
import { beforeAll, describe, expect, it } from "vitest";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface TempBasal {
  id: string;
  startTimestamp: string;
  endTimestamp?: string | null;
  rate: number;
  dataSource?: string | null;
  syncIdentifier?: string | null;
  legacyId?: string | null;
}

interface Page<T> {
  data: T[];
}

const HOUR = 60 * 60 * 1000;
const PATH = "/api/v4/insulin/temp-basals";

/** Each case gets its own hour, well clear of the dedup matching window of every other case. */
function slot(hoursAgo: number): string {
  return new Date(Math.floor((Date.now() - hoursAgo * HOUR) / 1000) * 1000).toISOString();
}

function objectId(): string {
  return randomBytes(12).toString("hex");
}

/** Temp basals are upserted by uploaders on their (dataSource, syncIdentifier) pair, or on the v1 `_id`. */
describe("temp basal sync-key upsert", () => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  function tempBasal(opts: { at: string; source: string; sync: string; rate: number }) {
    return { timestamp: opts.at, dataSource: opts.source, syncIdentifier: opts.sync, rate: opts.rate, durationMinutes: 30 };
  }

  async function bySource(source: string): Promise<TempBasal[]> {
    const res = await tenant.api.get<Page<TempBasal>>(`${PATH}?source=${encodeURIComponent(source)}&limit=50`);
    expect(res.status).toBe(200);
    return res.body.data;
  }

  async function inWindow(at: string): Promise<TempBasal[]> {
    const res = await tenant.api.get<Page<TempBasal>>(`${PATH}?from=${encodeURIComponent(at)}&to=${encodeURIComponent(at)}&limit=50`);
    expect(res.status).toBe(200);
    return res.body.data;
  }

  it("updates the stored span in place when the same record is resent singly", async () => {
    const at = slot(2);
    const created = await tenant.api.post<TempBasal[]>(PATH, [tempBasal({ at, source: "e2e-tb-single", sync: "tb-1", rate: 1.2 })]);
    expect(created.status).toBe(201);
    const [first] = created.body;

    const resent = await tenant.api.post<TempBasal[]>(PATH, [tempBasal({ at, source: "e2e-tb-single", sync: "tb-1", rate: 0.8 })]);
    expect(resent.status).toBe(201);
    expect(resent.body).toHaveLength(1);
    expect(resent.body[0]).toMatchObject({ id: first!.id, rate: 0.8, syncIdentifier: "tb-1" });

    const stored = await bySource("e2e-tb-single");
    expect(stored).toHaveLength(1);
    expect(stored[0]).toMatchObject({ id: first!.id, rate: 0.8 });
    expect(Date.parse(stored[0]!.endTimestamp!) - Date.parse(stored[0]!.startTimestamp)).toBe(30 * 60_000);
  });

  it("updates the stored span when it is resent in a multi-item POST next to a new one", async () => {
    const at = slot(4);
    const later = slot(3);
    const [first] = await tenant.api.ok<TempBasal[]>("POST", PATH, [tempBasal({ at, source: "e2e-tb-batch", sync: "tb-1", rate: 1.0 })]);

    const batch = await tenant.api.post<TempBasal[]>(PATH, [
      tempBasal({ at: later, source: "e2e-tb-batch", sync: "tb-2", rate: 2.0 }),
      tempBasal({ at, source: "e2e-tb-batch", sync: "tb-1", rate: 1.5 }),
    ]);
    expect(batch.status).toBe(201);
    expect(batch.body).toHaveLength(2);

    const stored = await bySource("e2e-tb-batch");
    expect(stored).toHaveLength(2);
    const bySync = new Map(stored.map((t) => [t.syncIdentifier, t]));
    expect(bySync.get("tb-1")).toMatchObject({ id: first!.id, rate: 1.5 });
    expect(bySync.get("tb-2")).toMatchObject({ rate: 2.0 });
    expect(bySync.get("tb-2")!.id).not.toBe(first!.id);
  });

  it("does not re-create a span the owner deleted, answering 201 with it left out", async () => {
    const at = slot(6);
    const [created] = await tenant.api.ok<TempBasal[]>("POST", PATH, [tempBasal({ at, source: "e2e-tb-deleted", sync: "tb-1", rate: 0.9 })]);

    const del = await tenant.api.delete(`/api/v1/treatments/${created!.id}`);
    expect(del.status).toBeLessThan(300);
    expect(await bySource("e2e-tb-deleted")).toHaveLength(0);

    const resent = await tenant.api.post<TempBasal[]>(PATH, [tempBasal({ at, source: "e2e-tb-deleted", sync: "tb-1", rate: 0.9 })]);
    expect(resent.status).toBe(201);
    expect(resent.body).toEqual([]);

    expect(await bySource("e2e-tb-deleted")).toHaveLength(0);
  });

  it("skips a deleted span inside a multi-item POST and still writes the spans around it", async () => {
    const source = "e2e-tb-deleted-batch";
    const deletedAt = slot(14);
    const [created] = await tenant.api.ok<TempBasal[]>("POST", PATH, [tempBasal({ at: deletedAt, source, sync: "tb-deleted", rate: 0.9 })]);
    const del = await tenant.api.delete(`/api/v1/treatments/${created!.id}`);
    expect(del.status).toBeLessThan(300);

    const batch = await tenant.api.post<TempBasal[]>(PATH, [
      tempBasal({ at: slot(16), source, sync: "tb-a", rate: 1.1 }),
      tempBasal({ at: deletedAt, source, sync: "tb-deleted", rate: 0.9 }),
      tempBasal({ at: slot(12), source, sync: "tb-b", rate: 1.3 }),
    ]);
    expect(batch.status).toBe(201);
    expect(batch.body.map((t) => t.syncIdentifier).sort()).toEqual(["tb-a", "tb-b"]);

    const stored = await bySource(source);
    expect(stored.map((t) => t.syncIdentifier).sort()).toEqual(["tb-a", "tb-b"]);
    expect(stored.find((t) => t.syncIdentifier === "tb-a")).toMatchObject({ rate: 1.1 });
    expect(stored.find((t) => t.syncIdentifier === "tb-b")).toMatchObject({ rate: 1.3 });
  });

  it("upserts a v1 Temp Basal treatment on its _id, and does not re-create it once deleted", async () => {
    const at = slot(8);
    const id = objectId();
    const v1 = (absolute: number) => ({ _id: id, eventType: "Temp Basal", created_at: at, absolute, duration: 30, enteredBy: "e2e" });

    expect((await tenant.api.post("/api/v1/treatments", [v1(1.1)])).status).toBe(200);
    const [stored] = await inWindow(at);
    expect(stored).toMatchObject({ legacyId: id, rate: 1.1 });

    expect((await tenant.api.post("/api/v1/treatments", [v1(0.7)])).status).toBe(200);
    const afterResend = await inWindow(at);
    expect(afterResend).toHaveLength(1);
    expect(afterResend[0]).toMatchObject({ id: stored!.id, legacyId: id, rate: 0.7 });

    const del = await tenant.api.delete(`/api/v1/treatments/${id}`);
    expect(del.status).toBeLessThan(300);
    expect(await inWindow(at)).toHaveLength(0);

    // v1 answers a refused re-upload like any other write, as Nightscout uploaders expect.
    expect((await tenant.api.post("/api/v1/treatments", [v1(0.7)])).status).toBe(200);
    expect(await inWindow(at)).toHaveLength(0);
  });
});
