import { beforeAll, describe, expect, it } from "vitest";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

// Nightscout's v3 DELETE keeps the document with `isValid: false` and a new `srvModified`, and
// `history` returns it, which is how a client syncing through history (AAPS) learns of a delete.

const MINUTE = 60_000;

interface V3Envelope<T> {
  status: number;
  result: T[];
}

interface V3Document {
  identifier?: string;
  _id?: string;
  srvModified: number;
  isValid?: boolean;
  insulin?: number;
  carbs?: number | null;
  eventType?: string;
  sgv?: number;
  device?: string;
  store?: Record<string, unknown>;
}

interface LastModified {
  result: { collections: Record<string, number> };
}

const idOf = (doc: V3Document) => doc.identifier ?? doc._id;
const cursorOf = (etag: string | null) => Number(etag!.match(/"(\d+)"/)![1]);

async function history(tenant: Tenant, collection: string, since: number) {
  const res = await tenant.api.get<V3Envelope<V3Document>>(`/api/v3/${collection}/history/${since}?limit=1000`);
  expect(res.status).toBe(200);
  return { docs: res.body.result, cursor: cursorOf(res.headers.get("etag")) };
}

/**
 * Reads `collection`'s history until `match` shows up live, deletes it by the identifier history
 * served, and returns the next history page from the cursor the first read left the client at.
 */
async function deleteAfterSync(tenant: Tenant, collection: string, match: (doc: V3Document) => boolean) {
  const synced = await history(tenant, collection, 0);
  const live = synced.docs.find(match);
  expect(live).toBeDefined();
  expect(live!.isValid).not.toBe(false);

  const deleted = await tenant.api.delete(`/api/v3/${collection}/${idOf(live!)}`);
  expect(deleted.status).toBeLessThan(300);

  const next = await history(tenant, collection, synced.cursor);
  return { live: live!, cursor: synced.cursor, next };
}

describe("v3 history after a delete", () => {
  let tenant: Tenant;
  const date = Math.floor((Date.now() - 15 * MINUTE) / 1000) * 1000;
  const iso = new Date(date).toISOString();

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  it("serves a deleted entry with isValid false, stamped after the client's cursor", async () => {
    const created = await tenant.api.request("POST", "/api/v3/entries", {
      type: "sgv", sgv: 187, date, direction: "Flat", device: "AAPS-e2e-delete", app: "AAPS", utcOffset: 0,
    });
    expect(created.status).toBeLessThan(300);

    const { live, cursor, next } = await deleteAfterSync(tenant, "entries", (e) => e.sgv === 187 && e.device === "AAPS-e2e-delete");

    const tombstone = next.docs.find((e) => idOf(e) === idOf(live));
    expect(tombstone).toMatchObject({ isValid: false, sgv: 187 });
    expect(tombstone!.srvModified).toBeGreaterThan(cursor);
    expect(next.cursor).toBeGreaterThanOrEqual(tombstone!.srvModified);
  });

  it("re-sends the other stream's reading when the bucket's canonical reading is deleted", async () => {
    // Two CGM streams report into one bucket, one reading each, far enough apart in value that
    // deduplication does not link them: only the canonical one is delivered.
    // Both inside one canonical bucket: buckets are fixed 5-minute windows, so two readings 30 s apart
    // at an arbitrary time straddle a boundary one run in ten and both win their own bucket.
    const bucket = Math.floor((date - 60 * MINUTE) / (5 * MINUTE)) * 5 * MINUTE;
    for (const [sgv, offset, device] of [[163, 0, "e2e-cgm-a"], [171, 30_000, "e2e-cgm-b"]] as const) {
      const created = await tenant.api.request("POST", "/api/v3/entries", {
        type: "sgv", sgv, date: bucket + MINUTE + offset, direction: "Flat", device, app: "e2e", utcOffset: 0,
      });
      expect(created.status).toBeLessThan(300);
    }

    const synced = await history(tenant, "entries", 0);
    const delivered = synced.docs.filter((e) => e.sgv === 163 || e.sgv === 171);
    expect(delivered).toHaveLength(1);
    const winner = delivered[0]!;
    const otherSgv = winner.sgv === 163 ? 171 : 163;

    expect((await tenant.api.delete(`/api/v3/entries/${idOf(winner)}`)).status).toBeLessThan(300);

    const next = await history(tenant, "entries", synced.cursor);
    expect(next.docs.find((e) => idOf(e) === idOf(winner))).toMatchObject({ isValid: false });
    const successor = next.docs.find((e) => e.sgv === otherSgv);
    expect(successor).toBeDefined();
    expect(successor!.isValid).not.toBe(false);
    expect(successor!.srvModified).toBeGreaterThan(synced.cursor);
  });

  it("tombstones every copy when the user deletes one copy of a duplicated reading", async () => {
    // Two sources report the same reading and deduplication links them. A user delete removes the
    // reading, not one source's report of it, so no copy comes back live.
    for (const [offset, device] of [[0, "e2e-dup-a"], [10_000, "e2e-dup-b"]] as const) {
      const created = await tenant.api.request("POST", "/api/v3/entries", {
        type: "sgv", sgv: 233, date: date - 90 * MINUTE + offset, direction: "Flat", device, app: "e2e", utcOffset: 0,
      });
      expect(created.status).toBeLessThan(300);
    }

    const synced = await history(tenant, "entries", 0);
    const delivered = synced.docs.filter((e) => e.sgv === 233);
    expect(delivered).toHaveLength(1);

    expect((await tenant.api.delete(`/api/v3/entries/${idOf(delivered[0]!)}`)).status).toBeLessThan(300);

    const next = await history(tenant, "entries", synced.cursor);
    const copies = next.docs.filter((e) => e.sgv === 233);
    expect(copies.map((e) => e.device).sort()).toEqual(["e2e-dup-a", "e2e-dup-b"]);
    expect(copies.every((e) => e.isValid === false)).toBe(true);
  });

  it("deletes every copy of a duplicated bolus deleted in v4, so it stops counting", async () => {
    for (const [offset, device] of [[0, "e2e-pump"], [10_000, "e2e-aaps"]] as const) {
      const created = await tenant.api.request("POST", "/api/v3/treatments", {
        eventType: "Correction Bolus", insulin: 2.7, date: date - 20 * MINUTE + offset, app: "e2e", device, utcOffset: 0, type: "NORMAL",
        data_source: device,
      });
      expect(created.status).toBe(201);
    }

    const bolusIds = async () =>
      (await tenant.api.ok<{ data: { id: string; insulin: number }[] }>("GET", "/api/v4/insulin/boluses?limit=100"))
        .data.filter((b) => b.insulin === 2.7).map((b) => b.id);

    const synced = await history(tenant, "treatments", 0);
    const shown = await bolusIds();
    expect(shown).toHaveLength(1);

    expect((await tenant.api.delete(`/api/v4/insulin/boluses/${shown[0]}`)).status).toBeLessThan(300);

    expect(await bolusIds()).toEqual([]);
    const next = await history(tenant, "treatments", synced.cursor);
    const copies = next.docs.filter((t) => t.insulin === 2.7);
    expect(copies).toHaveLength(2);
    expect(copies.every((t) => t.isValid === false)).toBe(true);
  });

  it("restores a deleted duplicated bolus through its non-primary copy with one live primary, re-sent live", async () => {
    for (const [offset, device] of [[0, "e2e-pump"], [10_000, "e2e-aaps"]] as const) {
      const created = await tenant.api.request("POST", "/api/v3/treatments", {
        eventType: "Correction Bolus", insulin: 3.1, date: date - 25 * MINUTE + offset, app: "e2e", device, utcOffset: 0, type: "NORMAL",
        data_source: device,
      });
      expect(created.status).toBe(201);
    }

    const bolusIds = async (path: string) =>
      (await tenant.api.ok<{ data: { id: string; insulin: number }[] }>("GET", path))
        .data.filter((b) => b.insulin === 3.1).map((b) => b.id);

    const shown = await bolusIds("/api/v4/insulin/boluses?limit=100");
    expect(shown).toHaveLength(1);
    const primary = shown[0];
    expect((await tenant.api.delete(`/api/v4/insulin/boluses/${primary}`)).status).toBeLessThan(300);
    const synced = await history(tenant, "treatments", 0);

    const deleted = await bolusIds("/api/v4/insulin/boluses/deleted?limit=100");
    expect(deleted).toHaveLength(2);
    expect(deleted).toContain(primary);
    const copy = deleted.find((id) => id !== primary)!;

    const restored = await tenant.api.request("POST", `/api/v4/insulin/boluses/${copy}/restore`);
    expect(restored.status).toBe(200);

    expect(await bolusIds("/api/v4/insulin/boluses?limit=100")).toEqual([primary]);
    const next = await history(tenant, "treatments", synced.cursor);
    const resent = next.docs.filter((t) => t.insulin === 3.1);
    expect(resent).toHaveLength(1);
    expect(resent[0].isValid).not.toBe(false);
  });

  it("sends a bolus two sources uploaded once, so a client syncing history counts it once", async () => {
    for (const [offset, device] of [[0, "e2e-pump"], [10_000, "e2e-aaps"]] as const) {
      const created = await tenant.api.request("POST", "/api/v3/treatments", {
        eventType: "Correction Bolus", insulin: 4.2, date: date - 30 * MINUTE + offset, app: "e2e", device, utcOffset: 0, type: "NORMAL",
        data_source: device,
      });
      expect(created.status).toBe(201);
    }

    const sent = (await history(tenant, "treatments", 0)).docs.filter((t) => t.insulin === 4.2);
    expect(sent).toHaveLength(1);
    expect(sent[0].isValid).not.toBe(false);
  });

  it("serves a deleted treatment with isValid false, stamped after the client's cursor", async () => {
    const created = await tenant.api.request("POST", "/api/v3/treatments", {
      eventType: "Correction Bolus", insulin: 1.35, date, app: "AAPS", device: "AAPS-e2e-delete", utcOffset: 0, type: "NORMAL",
    });
    expect(created.status).toBe(201);

    const { live, cursor, next } = await deleteAfterSync(tenant, "treatments", (t) => t.insulin === 1.35);

    const tombstone = next.docs.find((t) => idOf(t) === idOf(live));
    expect(tombstone).toMatchObject({ isValid: false, insulin: 1.35 });
    expect(tombstone!.srvModified).toBeGreaterThan(cursor);
  });

  it("serves a deleted devicestatus with isValid false, stamped after the client's cursor", async () => {
    const created = await tenant.api.request("POST", "/api/v3/devicestatus", {
      date, created_at: iso, device: "openaps://AAPS-e2e-delete", app: "AAPS", utcOffset: 0,
      openaps: { iob: { iob: 0.4, time: iso }, suggested: { bg: 125, eventualBG: 118, reason: "e2e delete", timestamp: iso } },
    });
    expect(created.status).toBeLessThan(300);

    const { live, cursor, next } = await deleteAfterSync(tenant, "devicestatus", (d) => d.device === "openaps://AAPS-e2e-delete");
    const tombstone = next.docs.find((d) => idOf(d) === idOf(live));
    expect(tombstone).toMatchObject({ isValid: false });
    expect(tombstone!.srvModified).toBeGreaterThan(cursor);
  });

  /** Creates a meal through v3, syncs history, and returns its history id, the cursor and its V4 halves. */
  async function syncMeal(insulin: number, carbs: number) {
    const created = await tenant.api.request("POST", "/api/v3/treatments", {
      eventType: "Meal Bolus", insulin, carbs, date, app: "AAPS", device: "AAPS-e2e-delete", utcOffset: 0,
    });
    expect(created.status).toBe(201);

    const synced = await history(tenant, "treatments", 0);
    const meal = synced.docs.find((t) => t.insulin === insulin);
    expect(meal).toMatchObject({ carbs });

    const boluses = await tenant.api.ok<{ data: { id: string; insulin: number }[] }>("GET", "/api/v4/insulin/boluses?limit=100");
    const intakes = await tenant.api.ok<{ data: { id: string; carbs: number }[] }>("GET", "/api/v4/nutrition/carbs?limit=100");
    return {
      meal: idOf(meal!)!,
      cursor: synced.cursor,
      bolusId: boluses.data.find((b) => b.insulin === insulin)!.id,
      carbId: intakes.data.find((c) => c.carbs === carbs)!.id,
    };
  }

  it("re-sends a meal whose carbs were deleted in v4 under its id, without the carbs", async () => {
    const { meal, cursor, carbId } = await syncMeal(3.3, 33);
    expect((await tenant.api.delete(`/api/v4/nutrition/carbs/${carbId}`)).status).toBeLessThan(300);

    const next = await history(tenant, "treatments", cursor);
    const survivor = next.docs.find((t) => idOf(t) === meal);
    expect(survivor).toMatchObject({ eventType: "Correction Bolus", insulin: 3.3 });
    expect(survivor!.isValid).toBeUndefined();
    // Nocturne's treatments always carry `carbs` (null when absent), live ones too; AAPS reads it
    // as a nullable Double, so null and missing are the same to it.
    expect(survivor!.carbs ?? null).toBeNull();
  });

  it("tombstones a meal whose bolus was deleted in v4 and re-sends its carbs under their own id", async () => {
    const { meal, cursor, bolusId } = await syncMeal(4.4, 44);
    expect((await tenant.api.delete(`/api/v4/insulin/boluses/${bolusId}`)).status).toBeLessThan(300);

    const next = await history(tenant, "treatments", cursor);
    expect(next.docs.find((t) => idOf(t) === meal)).toMatchObject({ isValid: false });
    const carbs = next.docs.find((t) => t.carbs === 44);
    expect(carbs).toMatchObject({ eventType: "Carb Correction" });
    expect(carbs!.isValid).toBeUndefined();
    expect(idOf(carbs!)).not.toBe(meal);
  });

  it("re-sends a two-store profile live when one store is deleted, and tombstones it with the last", async () => {
    const schedule = [{ time: "00:00", value: 1, timeAsSeconds: 0 }];
    const store = () => ({
      dia: 4, units: "mg/dl", timezone: "UTC",
      basal: schedule, carbratio: schedule, sens: schedule, target_low: schedule, target_high: schedule,
    });
    const created = await tenant.api.request("POST", "/api/v3/profile", {
      defaultProfile: "e2e-day", startDate: iso, created_at: iso, mills: date, units: "mg/dl",
      store: { "e2e-day": store(), "e2e-night": store() },
    });
    expect(created.status).toBeLessThan(300);

    const hasStore = (doc: V3Document, name: string) => Object.hasOwn(doc.store ?? {}, name);
    const settingsId = async (name: string) =>
      (await tenant.api.ok<{ id: string }[]>("GET", `/api/v4/profile/settings/by-name/${name}`))[0]!.id;

    const synced = await history(tenant, "profile", 0);
    const documentId = idOf(synced.docs.find((p) => hasStore(p, "e2e-day"))!);
    expect(documentId).toBeDefined();

    expect((await tenant.api.delete(`/api/v4/profile/settings/${await settingsId("e2e-day")}`)).status).toBeLessThan(300);

    const next = await history(tenant, "profile", synced.cursor);
    const resent = next.docs.filter((p) => idOf(p) === documentId);
    expect(resent).toHaveLength(1);
    expect(resent[0]!.isValid).toBeUndefined();
    expect(hasStore(resent[0]!, "e2e-night")).toBe(true);
    expect(hasStore(resent[0]!, "e2e-day")).toBe(false);
    expect(resent[0]!.srvModified).toBeGreaterThan(synced.cursor);

    expect((await tenant.api.delete(`/api/v4/profile/settings/${await settingsId("e2e-night")}`)).status).toBeLessThan(300);

    const last = await history(tenant, "profile", next.cursor);
    expect(last.docs.find((p) => idOf(p) === documentId)).toMatchObject({ isValid: false });
  });

  it("moves each collection's lastModified to the delete, so a client knows to read the history", async () => {
    const before = await tenant.api.ok<LastModified>("GET", "/api/v3/lastModified");
    const created = await tenant.api.request("POST", "/api/v3/treatments", {
      eventType: "Correction Bolus", insulin: 2.15, date, app: "AAPS", device: "AAPS-e2e-delete", utcOffset: 0, type: "NORMAL",
    });
    expect(created.status).toBe(201);

    const { next } = await deleteAfterSync(tenant, "treatments", (t) => t.insulin === 2.15);
    const tombstone = next.docs.find((t) => t.insulin === 2.15 && t.isValid === false);
    expect(tombstone).toBeDefined();

    expect(tombstone!.srvModified).toBeGreaterThan(before.result.collections.treatments ?? 0);
    const after = await tenant.api.ok<LastModified>("GET", "/api/v3/lastModified");
    expect(after.result.collections.treatments).toBeGreaterThanOrEqual(tombstone!.srvModified);
  });
});
