import { beforeAll, describe, expect, it } from "vitest";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface SyncKeyed {
  id: string;
  dataSource?: string | null;
  syncIdentifier?: string | null;
}

interface Page<T> {
  data: T[];
}

interface ProblemDetails {
  status: number;
  title: string;
  detail: string;
}

const HOUR = 60 * 60 * 1000;

/** A whole-second instant `hoursAgo` in the past; cases sit hours apart, clear of any dedup window. */
function slot(hoursAgo: number): string {
  return new Date(Math.floor((Date.now() - hoursAgo * HOUR) / 1000) * 1000).toISOString();
}

interface SyncKeyedType {
  name: string;
  path: string;
  /** Whether the controller exposes `DELETE by-sync-id`. */
  deletesByKey: boolean;
  body(at: string, source: string, sync: string, version: number): Record<string, unknown>;
  /** The stored field `version` drives, so a resend's change is observable. */
  value(record: Record<string, unknown>): unknown;
  expected(version: number): unknown;
}

const types: SyncKeyedType[] = [
  {
    name: "notes",
    path: "/api/v4/observations/notes",
    deletesByKey: true,
    body: (at, source, sync, v) => ({ timestamp: at, dataSource: source, syncIdentifier: sync, eventType: "Note", text: `e2e note v${v}` }),
    value: (r) => r.text,
    expected: (v) => `e2e note v${v}`,
  },
  {
    name: "device events",
    path: "/api/v4/observations/device-events",
    deletesByKey: true,
    body: (at, source, sync, v) => ({ timestamp: at, dataSource: source, syncIdentifier: sync, eventType: "SiteChange", notes: `e2e site v${v}` }),
    value: (r) => r.notes,
    expected: (v) => `e2e site v${v}`,
  },
  {
    name: "BG checks",
    path: "/api/v4/observations/bg-checks",
    deletesByKey: false,
    body: (at, source, sync, v) => ({ timestamp: at, dataSource: source, syncIdentifier: sync, glucose: 100 + v * 7, units: "MgDl", glucoseType: "Finger" }),
    value: (r) => r.glucose,
    expected: (v) => 100 + v * 7,
  },
];

describe.each(types)("v4 $name upsert on (dataSource, syncIdentifier)", (type) => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  async function bySource(source: string): Promise<Record<string, unknown>[]> {
    const res = await tenant.api.get<Page<Record<string, unknown>>>(`${type.path}?source=${encodeURIComponent(source)}&limit=50`);
    expect(res.status).toBe(200);
    return res.body.data;
  }

  it("updates the stored row when the record is resent singly", async () => {
    const at = slot(2);
    const created = await tenant.api.post<SyncKeyed>(type.path, type.body(at, "e2e-single", "sync-1", 1));
    expect(created.status).toBe(201);

    const resent = await tenant.api.post<Record<string, unknown>>(type.path, type.body(at, "e2e-single", "sync-1", 2));
    expect(resent.status).toBe(201);
    expect(resent.body.id).toBe(created.body.id);
    expect(type.value(resent.body)).toEqual(type.expected(2));

    const stored = await bySource("e2e-single");
    expect(stored).toHaveLength(1);
    expect(stored[0]!.id).toBe(created.body.id);
    expect(stored[0]!.syncIdentifier).toBe("sync-1");
    expect(type.value(stored[0]!)).toEqual(type.expected(2));
  });

  it("updates the stored row when the record is resent in a bulk batch", async () => {
    const at = slot(4);
    const later = slot(3);
    const created = await tenant.api.ok<SyncKeyed>("POST", type.path, type.body(at, "e2e-bulk", "sync-1", 1));

    const bulk = await tenant.api.post<Record<string, unknown>[]>(`${type.path}/bulk`, [
      type.body(at, "e2e-bulk", "sync-1", 3),
      type.body(later, "e2e-bulk", "sync-2", 4),
    ]);
    expect(bulk.status).toBe(201);
    expect(bulk.body).toHaveLength(2);

    const stored = await bySource("e2e-bulk");
    expect(stored).toHaveLength(2);
    const bySync = new Map(stored.map((r) => [r.syncIdentifier, r]));
    expect(bySync.get("sync-1")!.id).toBe(created.id);
    expect(type.value(bySync.get("sync-1")!)).toEqual(type.expected(3));
    expect(type.value(bySync.get("sync-2")!)).toEqual(type.expected(4));
  });

  it("keeps one row per source when two sources share a sync identifier", async () => {
    const a = await tenant.api.ok<SyncKeyed>("POST", type.path, type.body(slot(6), "e2e-source-a", "shared", 5));
    const b = await tenant.api.ok<SyncKeyed>("POST", type.path, type.body(slot(7), "e2e-source-b", "shared", 6));
    expect(b.id).not.toBe(a.id);

    const storedA = await bySource("e2e-source-a");
    const storedB = await bySource("e2e-source-b");
    expect(storedA.map((r) => r.id)).toEqual([a.id]);
    expect(storedB.map((r) => r.id)).toEqual([b.id]);
    expect(type.value(storedA[0]!)).toEqual(type.expected(5));
    expect(type.value(storedB[0]!)).toEqual(type.expected(6));

    if (!type.deletesByKey) return;

    const del = await tenant.api.delete(`${type.path}/by-sync-id?dataSource=e2e-source-a&syncIdentifier=shared`);
    expect(del.status).toBe(204);
    expect(await bySource("e2e-source-a")).toHaveLength(0);
    expect((await bySource("e2e-source-b")).map((r) => r.id)).toEqual([b.id]);

    const again = await tenant.api.delete(`${type.path}/by-sync-id?dataSource=e2e-source-a&syncIdentifier=shared`);
    expect(again.status).toBe(404);
  });

  it("does not re-create a record the owner deleted", async () => {
    const at = slot(9);
    const created = await tenant.api.ok<SyncKeyed>("POST", type.path, type.body(at, "e2e-deleted", "sync-1", 7));

    expect((await tenant.api.delete(`${type.path}/${created.id}`)).status).toBe(204);
    expect(await bySource("e2e-deleted")).toHaveLength(0);

    const single = await tenant.api.post<ProblemDetails>(type.path, type.body(at, "e2e-deleted", "sync-1", 8));
    expect(single.status).toBe(409);
    expect(single.headers.get("content-type")).toContain("application/problem+json");
    expect(single.body).toMatchObject({ status: 409, title: "Conflict" });
    expect(single.body.detail).toContain("'sync-1' from 'e2e-deleted'");

    const bulk = await tenant.api.post<SyncKeyed[]>(`${type.path}/bulk`, [type.body(at, "e2e-deleted", "sync-1", 8)]);
    expect(bulk.status).toBe(201);
    expect(bulk.body).toHaveLength(0);

    expect(await bySource("e2e-deleted")).toHaveLength(0);
    const deleted = await tenant.api.get<Page<SyncKeyed>>(`${type.path}/deleted?limit=100`);
    expect(deleted.status).toBe(200);
    expect(deleted.body.data.filter((r) => r.syncIdentifier === "sync-1" && r.dataSource === "e2e-deleted").map((r) => r.id)).toEqual([created.id]);
  });
});

interface SensorGlucose extends SyncKeyed {
  mgdl: number;
  mills: number;
}

describe("v4 sensor glucose sync identifier", () => {
  const PATH = "/api/v4/glucose/sensor";
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  const reading = (at: string, mgdl: number, sync: string | null = "sg-1", source: string | null = "e2e-cgm") => ({
    timestamp: at,
    dataSource: source,
    syncIdentifier: sync,
    mgdl,
    direction: "Flat",
  });

  async function bySource(source: string): Promise<SensorGlucose[]> {
    const res = await tenant.api.get<Page<SensorGlucose>>(`${PATH}?source=${encodeURIComponent(source)}&limit=50`);
    expect(res.status).toBe(200);
    return res.body.data;
  }

  it("stores the sync identifier on create and updates the reading on a resend", async () => {
    const at = slot(2);
    const created = await tenant.api.post<SensorGlucose>(PATH, reading(at, 110));
    expect(created.status).toBe(201);
    expect(created.body).toMatchObject({ syncIdentifier: "sg-1", dataSource: "e2e-cgm", mgdl: 110 });

    const resent = await tenant.api.post<SensorGlucose>(PATH, reading(at, 125));
    expect(resent.status).toBe(201);
    expect(resent.body).toMatchObject({ id: created.body.id, mgdl: 125 });

    const bulk = await tenant.api.post<SensorGlucose[]>(`${PATH}/bulk`, [reading(at, 131)]);
    expect(bulk.status).toBe(201);
    expect(bulk.body.map((r) => r.id)).toEqual([created.body.id]);

    const stored = await bySource("e2e-cgm");
    expect(stored).toHaveLength(1);
    expect(stored[0]).toMatchObject({ id: created.body.id, mgdl: 131, syncIdentifier: "sg-1", mills: Date.parse(at) });
  });

  it("keeps the stored sync identifier on update", async () => {
    const at = slot(4);
    const created = await tenant.api.ok<SensorGlucose>("POST", PATH, reading(at, 140, "sg-update", "e2e-cgm-update"));

    const updated = await tenant.api.put<SensorGlucose>(`${PATH}/${created.id}`, reading(at, 150, "sg-other", "e2e-cgm-update"));
    expect(updated.status).toBe(200);
    expect(updated.body).toMatchObject({ id: created.id, mgdl: 150, syncIdentifier: "sg-update" });

    const one = await tenant.api.get<SensorGlucose>(`${PATH}/${created.id}`);
    expect(one.body).toMatchObject({ mgdl: 150, syncIdentifier: "sg-update" });
  });

  it("rejects a sync identifier without a data source", async () => {
    const res = await tenant.api.post(PATH, reading(slot(6), 100, "sg-orphan", null));
    expect(res.status).toBe(400);
  });
});
