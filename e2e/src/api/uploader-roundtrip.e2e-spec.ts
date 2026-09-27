import { randomUUID } from "node:crypto";
import { beforeAll, describe, expect, it } from "vitest";
import { minutesAgo, postEntries, sgvSeries } from "../helpers/data.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

// Synthetic records in the shapes Loop (v1), AAPS (v3) and Trio (v1 with a lowercase client `id`)
// upload, read back through the other API version.

const MINUTE = 60_000;
const DAY = 24 * 60 * MINUTE;

interface V3Envelope<T> {
  status: number;
  result: T[];
}

interface V3Created {
  identifier: string;
}

interface Treatment {
  _id: string;
  identifier?: string;
  eventType: string;
  created_at: string;
  insulin?: number;
  carbs?: number;
  notes?: string;
}

interface Entry {
  _id: string;
  identifier?: string;
  date: number;
  sgv: number;
  device?: string;
}

interface DeviceStatus {
  device?: string;
  created_at?: string;
}

/** `ms` as an ISO string with no zone designator, as some uploaders write local-less UTC. */
function isoWithoutOffset(ms: number): string {
  return new Date(ms).toISOString().slice(0, -1);
}

/** `ms` as an ISO string in +10:00. */
function isoPlusTen(ms: number): string {
  return new Date(ms + 10 * 60 * MINUTE).toISOString().slice(0, -1) + "+10:00";
}

const v3 = <T>(tenant: Tenant, path: string) => tenant.api.ok<V3Envelope<T>>("GET", path).then((r) => r.result);
const v1Treatments = (tenant: Tenant, query = "count=100") => tenant.api.ok<Treatment[]>("GET", `/api/v1/treatments.json?${query}`);

describe("uploader round-trip", () => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  it("Loop: v1 uploads read back through v3", async () => {
    const readings = sgvSeries({ count: 6, device: "loop://e2e-iphone", valueAt: (i) => 150 + i });
    await postEntries(tenant.api, readings);
    const syncIdentifier = randomUUID();
    await tenant.api.ok("POST", "/api/v1/treatments", [
      { eventType: "Correction Bolus", insulin: 0.35, created_at: minutesAgo(30), enteredBy: "loop://e2e-iphone", syncIdentifier },
    ]);
    const at = new Date(Date.now() - 5 * MINUTE).toISOString();
    await tenant.api.ok("POST", "/api/v1/devicestatus", [
      {
        device: "loop://e2e-iphone",
        created_at: at,
        loop: { name: "Loop", version: "3.4.0", timestamp: at, iob: { iob: 1.2, timestamp: at }, cob: { cob: 10, timestamp: at }, predicted: { startDate: at, values: [150, 148, 145] } },
        pump: { clock: at, reservoir: 120, battery: { percent: 60 }, pumpID: "e2e-pump" },
        uploader: { battery: 77, timestamp: at },
      },
    ]);

    const entries = await v3<Entry>(tenant, "/api/v3/entries?limit=50&sort$desc=date");
    expect(entries.map((e) => e.date)).toEqual(expect.arrayContaining(readings.map((r) => r.date)));

    const treatments = await v3<Treatment>(tenant, "/api/v3/treatments?limit=50");
    expect(treatments.filter((t) => t.eventType === "Correction Bolus" && t.insulin === 0.35)).toHaveLength(1);

    const statuses = await v3<DeviceStatus>(tenant, "/api/v3/devicestatus?limit=50");
    expect(statuses.filter((d) => d.device === "loop://e2e-iphone")).toHaveLength(1);
  });

  it("AAPS: v3 uploads read back through v1", async () => {
    const date = Math.floor((Date.now() - 12 * MINUTE) / 1000) * 1000;
    const iso = new Date(date).toISOString();
    const entry = await tenant.api.request<V3Created>("POST", "/api/v3/entries", {
      type: "sgv", sgv: 143, date, direction: "Flat", device: "AAPS-e2e", app: "AAPS", utcOffset: 0,
    });
    expect(entry.status).toBeLessThan(300);
    const treatment = await tenant.api.request<V3Created>("POST", "/api/v3/treatments", {
      eventType: "Correction Bolus", insulin: 0.6, date, app: "AAPS", device: "AAPS-e2e", utcOffset: 0,
      isValid: true, type: "NORMAL", pumpId: 4242, pumpType: "ACCU_CHEK_COMBO", pumpSerial: "e2e-serial",
    });
    expect(treatment.status).toBe(201);
    const status = await tenant.api.request("POST", "/api/v3/devicestatus", {
      date, device: "openaps://AAPS-e2e", app: "AAPS", utcOffset: 0, uploaderBattery: 70, isCharging: false,
      openaps: { iob: { iob: 0.7, time: iso }, suggested: { bg: 130, eventualBG: 120, reason: "e2e", timestamp: iso } },
      pump: { clock: iso, reservoir: 90, battery: { percent: 50 } },
    });
    expect(status.status).toBeLessThan(300);

    const entries = await tenant.api.ok<Entry[]>("GET", `/api/v1/entries.json?find[date][$eq]=${date}`);
    expect(entries.filter((e) => e.sgv === 143)).toHaveLength(1);

    const treatments = (await v1Treatments(tenant)).filter((t) => t.eventType === "Correction Bolus" && t.insulin === 0.6);
    expect(treatments).toHaveLength(1);
    expect(Date.parse(treatments[0]!.created_at)).toBe(date);

    const statuses = await tenant.api.ok<DeviceStatus[]>("GET", "/api/v1/devicestatus.json?count=50");
    const aaps = statuses.filter((d) => d.device === "openaps://AAPS-e2e");
    expect(aaps).toHaveLength(1);
    expect(Date.parse(aaps[0]!.created_at!)).toBe(date);
  });

  it("stores v1 treatment times at the instant they name, with or without an offset", async () => {
    const plain = Math.floor((Date.now() - 50 * MINUTE) / 1000) * 1000;
    const offset = plain - 7 * MINUTE;
    await tenant.api.ok("POST", "/api/v1/treatments", [
      { eventType: "Note", notes: "e2e no offset", created_at: isoWithoutOffset(plain), enteredBy: "e2e" },
      { eventType: "Note", notes: "e2e plus ten", created_at: isoPlusTen(offset), enteredBy: "e2e" },
    ]);

    const stored = await v1Treatments(tenant);
    expect(Date.parse(stored.find((t) => t.notes === "e2e no offset")!.created_at)).toBe(plain);
    expect(Date.parse(stored.find((t) => t.notes === "e2e plus ten")!.created_at)).toBe(offset);
  });

  it("pages v3 treatment history by the lastModified cursor until drained", async () => {
    const base = Date.now() - 3 * 60 * MINUTE;
    const carbs = [10, 11, 12, 13, 14];
    const posted: string[] = [];
    for (const [i, grams] of carbs.entries()) {
      const res = await tenant.api.request<V3Created>("POST", "/api/v3/treatments", {
        eventType: "Carb Correction", carbs: grams, date: base + i * MINUTE, app: "AAPS", device: "AAPS-e2e", utcOffset: 0, isValid: true,
      });
      expect(res.status).toBe(201);
      posted.push(res.body.identifier);
    }

    const seen: string[] = [];
    let cursor = 0;
    for (let page = 0; page < 50; page++) {
      const res = await tenant.api.get<V3Envelope<Treatment>>(`/api/v3/treatments/history/${cursor}?limit=2`);
      expect(res.status).toBe(200);
      if (res.body.result.length === 0) break;
      seen.push(...res.body.result.map((t) => t.identifier ?? t._id));
      const etag = res.headers.get("etag");
      expect(etag).toMatch(/^W\/"\d+"$/);
      const next = Number(etag!.match(/"(\d+)"/)![1]);
      expect(next).toBeGreaterThan(cursor);
      cursor = next;
    }

    expect(new Set(seen).size).toBe(seen.length);
    expect(seen).toEqual(expect.arrayContaining(posted));
    const drained = await tenant.api.get<V3Envelope<Treatment>>(`/api/v3/treatments/history/${cursor}?limit=2`);
    expect(drained.body.result).toEqual([]);
  });
});

describe("v1 treatment finds with field filters", () => {
  let tenant: Tenant;
  const recent = { eventType: "Note", notes: "e2e window recent", created_at: new Date(Date.now() - DAY).toISOString(), enteredBy: "e2e" };
  const old = { eventType: "Note", notes: "e2e window old", created_at: new Date(Date.now() - 5 * DAY).toISOString(), enteredBy: "e2e" };

  beforeAll(async () => {
    tenant = await seedTenant();
    await tenant.api.ok("POST", "/api/v1/treatments", [recent, old]);
  });

  const notes = (rows: Treatment[]) => rows.map((t) => t.notes).sort();

  it("apply legacy Nightscout's four-day window", async () => {
    expect(notes(await v1Treatments(tenant, "count=100&find[eventType]=Note"))).toEqual([recent.notes]);
  });

  it("drop the window once the find names created_at", async () => {
    const since = encodeURIComponent(new Date(Date.now() - 7 * DAY).toISOString());
    expect(notes(await v1Treatments(tenant, `count=100&find[eventType]=Note&find[created_at][$gte]=${since}`))).toEqual([old.notes, recent.notes].sort());
  });

  it("leave an unfiltered find unwindowed", async () => {
    expect(notes(await v1Treatments(tenant))).toEqual([old.notes, recent.notes].sort());
  });

  it("window the count route the same way", async () => {
    const windowed = await tenant.api.ok<{ count: number }>("GET", "/api/v1/count/treatments/where?find[eventType]=Note");
    expect(windowed.count).toBe(1);
    const since = encodeURIComponent(new Date(Date.now() - 7 * DAY).toISOString());
    const named = await tenant.api.ok<{ count: number }>("GET", `/api/v1/count/treatments/where?find[eventType]=Note&find[created_at][$gte]=${since}`);
    expect(named.count).toBe(2);
  });
});

describe("a treatment the user deleted", () => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  it("is not resurrected when Loop uploads it again", async () => {
    const upload = [{ eventType: "Correction Bolus", insulin: 0.45, created_at: minutesAgo(40), enteredBy: "loop://e2e-iphone", syncIdentifier: randomUUID() }];
    await tenant.api.ok("POST", "/api/v1/treatments", upload);
    const stored = (await v1Treatments(tenant)).find((t) => t.insulin === 0.45);
    expect(stored).toBeDefined();
    expect((await tenant.api.delete(`/api/v1/treatments/${stored!._id}`)).status).toBeLessThan(300);

    await tenant.api.request("POST", "/api/v1/treatments", upload);
    expect((await v1Treatments(tenant)).filter((t) => t.insulin === 0.45)).toEqual([]);
  });

  it("is not resurrected when Trio uploads it again under the same client id", async () => {
    const id = randomUUID().toLowerCase();
    const upload = [{ eventType: "Carb Correction", carbs: 23, created_at: minutesAgo(55), enteredBy: "Trio", id }];
    await tenant.api.ok("POST", "/api/v1/treatments", upload);
    expect((await v1Treatments(tenant)).filter((t) => t.carbs === 23)).toHaveLength(1);

    expect((await tenant.api.delete(`/api/v1/treatments?find[id][$eq]=${id}`)).status).toBeLessThan(300);
    expect((await v1Treatments(tenant)).filter((t) => t.carbs === 23)).toEqual([]);

    await tenant.api.request("POST", "/api/v1/treatments", upload);
    expect((await v1Treatments(tenant)).filter((t) => t.carbs === 23)).toEqual([]);
  });

  it("is not resurrected when AAPS uploads it again through v3", async () => {
    const upload = {
      eventType: "Correction Bolus", insulin: 0.85, date: Math.floor((Date.now() - 70 * MINUTE) / 1000) * 1000,
      app: "AAPS", device: "AAPS-e2e", utcOffset: 0, isValid: true, type: "NORMAL",
    };
    const created = await tenant.api.request<V3Created>("POST", "/api/v3/treatments", upload);
    expect(created.status).toBe(201);
    expect((await tenant.api.delete(`/api/v3/treatments/${created.body.identifier}`)).status).toBeLessThan(300);

    await tenant.api.request("POST", "/api/v3/treatments", upload);
    const search = await v3<Treatment>(tenant, "/api/v3/treatments?limit=50");
    expect(search.filter((t) => t.insulin === 0.85)).toEqual([]);
    expect((await v1Treatments(tenant)).filter((t) => t.insulin === 0.85)).toEqual([]);
  });
});

describe("a v3 treatment addressed by the identifier its POST returned", () => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  it("can be read and deleted by that identifier", async () => {
    const created = await tenant.api.request<V3Created>("POST", "/api/v3/treatments", {
      eventType: "Correction Bolus", insulin: 0.95, date: Math.floor((Date.now() - 20 * MINUTE) / 1000) * 1000,
      app: "AAPS", device: "AAPS-e2e", utcOffset: 0, isValid: true, type: "NORMAL",
    });
    expect(created.status).toBe(201);
    const identifier = created.body.identifier;

    expect((await tenant.api.get(`/api/v3/treatments/${identifier}`)).status).toBe(200);
    const search = await v3<Treatment>(tenant, "/api/v3/treatments?limit=50");
    expect(search.filter((t) => t.insulin === 0.95).map((t) => t.identifier)).toEqual([identifier]);

    expect((await tenant.api.delete(`/api/v3/treatments/${identifier}`)).status).toBeLessThan(300);
    expect((await v3<Treatment>(tenant, "/api/v3/treatments?limit=50")).filter((t) => t.insulin === 0.95)).toEqual([]);
  });
});
