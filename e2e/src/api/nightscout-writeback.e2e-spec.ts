import { createHash } from "node:crypto";
import { beforeAll, describe, expect, it } from "vitest";
import { env } from "../helpers/env.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";
import { NIGHTSCOUT_API_SECRET_HEADER } from "../../mocks/vendors/nightscout.ts";

// A tenant whose Nightscout connector writes back to the instance it pulls from
// (e2e/mocks/vendors/nightscout-writeback.ts keeps what it is sent, normalising ids and upserting as
// Nightscout 15.0.7+ does, and serves it back). Every record Nocturne writes upstream comes back on
// the next pull, often under a `_id` the upstream minted; it must land on the record it was written
// from without changing it or its source, and a record the user deleted must stay deleted (#1804).
//
// Each reading is on its own device and at least 6 minutes from every other: readings of two
// devices in one 5-minute bucket compete as streams, and only the canonical one is projected to
// the legacy entry that write-back sends (CanonicalGlucoseStream).
const FAKE_SECRET = "e2e-fake-nightscout-secret";
const VENDOR = `${env.mocksUrl}/nightscout-writeback`;
const MINUTE = 60_000;

interface VendorRequest {
  method: string;
  path: string;
  body: string;
}

interface Page<T> {
  data: T[];
}

interface SensorGlucose {
  id: string;
  mgdl: number;
}

interface ApsSnapshot {
  id: string;
}

interface Bolus {
  id: string;
  insulin: number;
}

interface V1DeviceStatus {
  _id: string;
  device: string;
}

interface SourcedBolus extends Bolus {
  dataSource?: string | null;
}

interface UpstreamDoc {
  _id: string;
  identifier?: string;
  eventType?: string;
  created_at?: string;
  insulin?: number;
  sgv?: number;
  date?: number;
  device?: string;
}

interface SyncResult {
  success: boolean;
  errors: string[];
}

/** The 24-hex prefix a uuid goes upstream under (MongoObjectId.FromGuid). */
function uuidPrefix(uuid: string): string {
  return uuid.replace(/[^0-9a-fA-F]/g, "").toLowerCase().slice(0, 24);
}

/**
 * The 24-hex id a treatment goes upstream under (MongoObjectId.Coerce): an ObjectId as it is, a uuid
 * as its prefix, anything else as the head of its SHA-256.
 */
function wireId(key: string): string {
  if (/^[0-9a-f]{24}$/.test(key)) return key;
  if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(key)) return uuidPrefix(key);
  return createHash("sha256").update(key, "utf8").digest("hex").slice(0, 24);
}

/** A fresh lowercase 24-hex ObjectId, as a Nightscout or an AAPS client mints one. */
function objectId(): string {
  const seconds = Math.floor(Date.now() / 1000).toString(16).padStart(8, "0");
  return seconds + Array.from(crypto.getRandomValues(new Uint8Array(8)), (b) => b.toString(16).padStart(2, "0")).join("");
}

const upstreamHeaders = { "content-type": "application/json", "api-secret": NIGHTSCOUT_API_SECRET_HEADER };

/** Writes straight to the fake Nightscout, as another uploader or an earlier Nocturne would have. */
async function upstreamPost(path: string, docs: Record<string, unknown>[]): Promise<UpstreamDoc[]> {
  const response = await fetch(`${VENDOR}${path}`, { method: "POST", headers: upstreamHeaders, body: JSON.stringify(docs) });
  expect(response.status).toBe(200);
  return (await response.json()) as UpstreamDoc[];
}

async function upstreamRead(path: string, query: Record<string, string>): Promise<UpstreamDoc[]> {
  const response = await fetch(`${VENDOR}${path}?${new URLSearchParams({ count: "1000", ...query })}`, { headers: upstreamHeaders });
  expect(response.status).toBe(200);
  return (await response.json()) as UpstreamDoc[];
}

/** The upstream treatments of one event type at one instant. */
async function upstreamTreatmentsAt(at: string, eventType: string): Promise<UpstreamDoc[]> {
  const from = new Date(Date.parse(at) - MINUTE).toISOString();
  const to = new Date(Date.parse(at) + MINUTE).toISOString();
  return (await upstreamRead("/api/v1/treatments.json", { "find[created_at][$gte]": from, "find[created_at][$lte]": to }))
    .filter((t) => t.eventType === eventType && Date.parse(String(t.created_at)) === Date.parse(at));
}

async function writtenBack(path: string): Promise<Record<string, unknown>[]> {
  const requests = (await (await fetch(`${VENDOR}/__requests`)).json()) as VendorRequest[];
  return requests
    .filter((r) => (r.method === "POST" || r.method === "PUT") && r.path === path)
    .flatMap((r) => {
      const body = JSON.parse(r.body) as Record<string, unknown> | Record<string, unknown>[];
      return Array.isArray(body) ? body : [body];
    });
}

describe("Nightscout connector write-back round trip", () => {
  let tenant: Tenant;
  let run: string;

  const readings = (device: string) =>
    tenant.api.ok<Page<SensorGlucose>>("GET", `/api/v4/glucose/sensor?limit=50&device=${encodeURIComponent(device)}`);
  const apsSnapshots = (device: string) =>
    tenant.api.ok<Page<ApsSnapshot>>("GET", `/api/v4/device-status/aps?limit=50&device=${encodeURIComponent(device)}`);
  const sync = () => tenant.api.ok<SyncResult>("POST", "/api/v4/services/connectors/nightscout/sync", {});
  const writeBack = (writeBackEnabled: boolean) =>
    tenant.api.ok("PUT", "/api/v4/connectors/config/nightscout", {
      url: `${env.mocksUrlFromApi}/nightscout-writeback`,
      isActive: true,
      writeBackEnabled,
    });

  beforeAll(async () => {
    tenant = await seedTenant();
    run = tenant.slug;
    await writeBack(true);
    await tenant.api.ok("PUT", "/api/v4/connectors/config/nightscout/secrets", { apiSecret: FAKE_SECRET });
  });

  async function createReading(device: string, minutesAgo: number): Promise<SensorGlucose> {
    const created = await tenant.api.post<SensorGlucose>("/api/v4/glucose/sensor", {
      timestamp: new Date(Date.now() - minutesAgo * MINUTE).toISOString(),
      device,
      mgdl: 123,
    });
    expect(created.status).toBe(201);
    return created.body;
  }

  async function uploadIdLessStatus(device: string, minutesAgo: number): Promise<string> {
    const at = new Date(Date.now() - minutesAgo * MINUTE).toISOString();
    const created = await tenant.api.ok<V1DeviceStatus[]>("POST", "/api/v1/devicestatus", [
      { device, created_at: at, openaps: { iob: { iob: 0.8, timestamp: at } } },
    ]);
    expect(created[0]!._id).toMatch(/^[0-9a-f]{24}$/);
    return created[0]!._id;
  }

  it("pulls a reading it wrote back onto the reading it came from", async () => {
    const device = `e2e-writeback-kept-${run}`;
    const reading = await createReading(device, 30);

    const sent = (await writtenBack("/api/v1/entries")).filter((e) => e.device === device);
    expect(sent.map((e) => [e._id, e.identifier])).toEqual([[undefined, reading.id]]);

    const result = await sync();
    expect(result.success).toBe(true);

    const stored = await readings(device);
    expect(stored.data.map((r) => r.id)).toEqual([reading.id]);
  });

  it("keeps a reading the user deleted deleted when its written-back copy comes back", async () => {
    const device = `e2e-writeback-deleted-${run}`;
    const reading = await createReading(device, 25);
    expect((await writtenBack("/api/v1/entries")).some((e) => e.identifier === reading.id)).toBe(true);
    expect((await tenant.api.delete(`/api/v4/glucose/sensor/${reading.id}`)).status).toBeLessThan(300);

    expect((await sync()).success).toBe(true);

    expect((await readings(device)).data).toEqual([]);
  });

  it("writes an id-less status back under the id it is stored under and pulls it back onto itself", async () => {
    const device = `openaps://e2e-writeback-kept-${run}`;
    const id = await uploadIdLessStatus(device, 20);

    const sent = (await writtenBack("/api/v1/devicestatus")).filter((d) => d.device === device);
    expect(sent.map((d) => [d._id, d.identifier])).toEqual([[id, id]]);

    expect((await sync()).success).toBe(true);

    expect((await apsSnapshots(device)).data).toHaveLength(1);
    const statuses = await tenant.api.ok<V1DeviceStatus[]>("GET", "/api/v1/devicestatus.json?count=100");
    expect(statuses.filter((d) => d.device === device).map((d) => d._id)).toEqual([id]);
  });

  it("keeps a status the user deleted deleted when its written-back copy comes back", async () => {
    const device = `openaps://e2e-writeback-deleted-${run}`;
    const id = await uploadIdLessStatus(device, 15);
    expect((await tenant.api.delete(`/api/v1/devicestatus/${id}`)).status).toBeLessThan(300);

    expect((await sync()).success).toBe(true);

    expect((await apsSnapshots(device)).data).toEqual([]);
  });

  it("pulls a reading whose legacy id is a uuid back onto that reading", async () => {
    const device = `e2e-writeback-uuid-${run}`;
    const legacyId = crypto.randomUUID().toUpperCase();
    const date = Date.now() - 10 * MINUTE;
    await tenant.api.ok("POST", "/api/v1/entries", [
      { _id: legacyId, type: "sgv", sgv: 131, date, dateString: new Date(date).toISOString(), device },
    ]);

    const sent = (await writtenBack("/api/v1/entries")).filter((e) => e.device === device);
    expect(sent.map((e) => [e._id, e.identifier])).toEqual([[undefined, legacyId]]);

    expect((await sync()).success).toBe(true);

    expect((await readings(device)).data).toHaveLength(1);
  });

  // A v4-native treatment has no legacy id, so the one path that writes it back, its first v1 edit,
  // gives it its uuid's prefix and sends that as both `_id` and `identifier`; Nightscout 15.0.7+
  // upserts it by that identifier under a `_id` of its own. The spec leaves the bolus without a
  // legacy id and sends that payload upstream itself: the pull-back of an unkeyed record is what is
  // under test.
  async function createBolusWrittenBack(minutesAgo: number): Promise<{ bolus: SourcedBolus; at: string }> {
    const at = new Date(Date.now() - minutesAgo * MINUTE).toISOString();
    const created = await tenant.api.post<SourcedBolus>("/api/v4/insulin/boluses", { timestamp: at, insulin: 2.5 });
    expect(created.status).toBe(201);
    const [stored] = await upstreamPost("/api/v1/treatments", [
      { _id: uuidPrefix(created.body.id), identifier: uuidPrefix(created.body.id), eventType: "Correction Bolus", insulin: 2.5, created_at: at },
    ]);
    expect(stored!._id).not.toBe(uuidPrefix(created.body.id));
    return { bolus: created.body, at };
  }

  const bolusesAround = (at: string) => {
    const from = encodeURIComponent(new Date(Date.parse(at) - MINUTE).toISOString());
    const to = encodeURIComponent(new Date(Date.parse(at) + MINUTE).toISOString());
    return tenant.api.ok<Page<SourcedBolus>>("GET", `/api/v4/insulin/boluses?limit=50&from=${from}&to=${to}`);
  };

  it("pulls a v4-native treatment written back under its uuid prefix onto that treatment", async () => {
    const { bolus, at } = await createBolusWrittenBack(40);

    expect((await sync()).success).toBe(true);

    expect((await bolusesAround(at)).data.map((b) => [b.id, b.dataSource ?? null])).toEqual([[bolus.id, bolus.dataSource ?? null]]);
  });

  it("keeps a v4-native treatment the user deleted deleted when its written-back copy comes back", async () => {
    const { bolus, at } = await createBolusWrittenBack(45);
    expect((await tenant.api.delete(`/api/v4/insulin/boluses/${bolus.id}`)).status).toBeLessThan(300);

    expect((await sync()).success).toBe(true);

    expect((await bolusesAround(at)).data).toEqual([]);
  });

  it("pulls a reading whose legacy id is not an ObjectId back onto that reading", async () => {
    const device = `e2e-writeback-legacy-${run}`;
    const legacyId = `e2e-legacy-${run}`;
    const date = Date.now() - 18 * MINUTE;
    await tenant.api.ok("POST", "/api/v1/entries", [
      { _id: legacyId, type: "sgv", sgv: 133, date, dateString: new Date(date).toISOString(), device },
    ]);

    const [sent] = (await writtenBack("/api/v1/entries")).filter((e) => e.device === device);
    expect(sent!._id).toBeUndefined();
    expect(sent!.identifier).toBe(legacyId);

    expect((await sync()).success).toBe(true);

    expect((await readings(device)).data).toHaveLength(1);
  });

  // Nightscout upserts an entry by sysTime and type with `$set`, so a `_id` other than the stored
  // reading's fails the whole ordered batch. A reading another uploader already sent there is
  // written onto, under no `_id` of Nocturne's.
  it("writes a reading back onto the reading Nightscout already holds at that time", async () => {
    const device = `e2e-writeback-same-time-${run}`;
    const legacyId = `e2e-same-time-${run}`;
    const date = Date.now() - 36 * MINUTE;
    const [held] = await upstreamPost("/api/v1/entries", [{ _id: objectId(), type: "sgv", sgv: 99, date, device }]);

    await tenant.api.ok("POST", "/api/v1/entries", [
      { _id: legacyId, type: "sgv", sgv: 141, date, dateString: new Date(date).toISOString(), device },
    ]);

    const upstream = (await upstreamRead("/api/v1/entries.json", { "find[date][$gte]": String(date), "find[date][$lte]": String(date) }))
      .filter((e) => e.device === device);
    expect(upstream.map((e) => [e._id, e.identifier, e.sgv])).toEqual([[held!._id, legacyId, 141]]);

    expect((await sync()).success).toBe(true);
    expect((await readings(device)).data.map((r) => r.mgdl)).toEqual([141]);
  });

  // Nightscout 15.0.7+ re-mints the `_id` of a treatment written back with an identifier, so each
  // sync pulls the copy back under an id Nocturne never stored. It must stay the bolus's echo: the
  // bolus keeps its source, so an edit made in Nocturne is still written back, and the stale copy
  // pulled before the edit reaches upstream never overwrites it.
  it("keeps a written-back treatment its own across syncs and writes a later edit back", async () => {
    const syncIdentifier = crypto.randomUUID();
    const at = new Date(Date.now() - 65 * MINUTE).toISOString();
    const upload = { eventType: "Correction Bolus", insulin: 1.25, created_at: at, enteredBy: `e2e-writeback-edit-${run}`, syncIdentifier };
    await tenant.api.ok("POST", "/api/v1/treatments", [upload]);
    const [bolus] = (await bolusesAround(at)).data;
    expect(bolus).toBeDefined();
    expect(bolus!.dataSource).not.toBe("nightscout-connector");
    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => t.identifier)).toEqual([wireId(syncIdentifier)]);

    expect((await sync()).success).toBe(true);
    expect((await sync()).success).toBe(true);

    const pulled = (await bolusesAround(at)).data;
    expect(pulled.map((b) => [b.id, b.insulin, b.dataSource ?? null])).toEqual([[bolus!.id, 1.25, bolus!.dataSource ?? null]]);

    await tenant.api.ok("PUT", `/api/v4/insulin/boluses/${bolus!.id}`, { timestamp: at, insulin: 2, dataSource: bolus!.dataSource });
    expect((await sync()).success).toBe(true);
    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => [b.id, b.insulin])).toEqual([[bolus!.id, 2]]);

    const sentBefore = (await writtenBack("/api/v1/treatments")).filter((t) => t.identifier === wireId(syncIdentifier)).length;
    await tenant.api.ok("PUT", `/api/v1/treatments/${uuidPrefix(bolus!.id)}`, { ...upload, insulin: 2.25 });
    expect((await writtenBack("/api/v1/treatments")).filter((t) => t.identifier === wireId(syncIdentifier)).length).toBe(sentBefore + 1);
    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t.identifier, t.insulin])).toEqual([[wireId(syncIdentifier), 2.25]]);

    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => [b.id, b.insulin])).toEqual([[bolus!.id, 2.25]]);
  });

  // Every earlier write-back sent a treatment under the coercion of its key as both `_id` and
  // `identifier`, so that is where its copy upstream is found. An edit made in Nocturne must land on
  // that copy: a second one would give followers of that Nightscout, and an AAPS syncing from it, the
  // dose twice. The treatment is stored with write-back off and its copy is put upstream as the
  // earlier write-back left it.
  it.each([
    ["an ObjectId", 80, () => objectId()],
    ["a uuid", 90, () => crypto.randomUUID()],
    ["another", 100, () => `e2e-tr-${crypto.randomUUID()}`],
  ])("writes an edit of a treatment keyed by %s onto the copy an earlier write-back left", async (_shape, minutesAgo, key) => {
    const legacyId = key();
    const wire = wireId(legacyId);
    const at = new Date(Date.now() - minutesAgo * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Correction Bolus", insulin: 0.7, created_at: at, enteredBy: `e2e-writeback-main-${run}` };
    await writeBack(false);
    try {
      await tenant.api.ok("POST", "/api/v1/treatments", [upload]);
    } finally {
      await writeBack(true);
    }
    const [copy] = await upstreamPost("/api/v1/treatments", [{ ...upload, _id: wire, identifier: wire }]);
    expect(copy!._id).not.toBe(wire);

    const [bolus] = (await bolusesAround(at)).data;
    await tenant.api.ok("PUT", `/api/v1/treatments/${uuidPrefix(bolus!.id)}`, { ...upload, insulin: 1.1 });

    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[copy!._id, wire, 1.1]]);
    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => [b.id, b.insulin])).toEqual([[bolus!.id, 1.1]]);
    expect((await upstreamTreatmentsAt(at, "Correction Bolus"))).toHaveLength(1);
  });

  // A treatment upstream holds under its ObjectId alone, such as the original of one a Nightscout
  // migration imported, has no copy under its identifier. An edit carrying one would match neither
  // on 15.0.7+ and store a second copy, so it goes out under the ObjectId alone.
  it("writes an edit of a treatment upstream holds under its ObjectId alone onto that treatment", async () => {
    const legacyId = objectId();
    const at = new Date(Date.now() - 70 * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Correction Bolus", insulin: 0.6, created_at: at, enteredBy: `e2e-writeback-oid-${run}` };
    await upstreamPost("/api/v1/treatments", [upload]);
    await writeBack(false);
    try {
      await tenant.api.ok("POST", "/api/v1/treatments", [upload]);
    } finally {
      await writeBack(true);
    }

    await tenant.api.ok("PUT", `/api/v1/treatments/${legacyId}`, { ...upload, insulin: 0.9 });

    const sent = (await writtenBack("/api/v1/treatments")).filter((t) => t._id === legacyId);
    expect(sent.map((t) => t.identifier)).toEqual([undefined]);
    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[legacyId, undefined, 0.9]]);

    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => b.insulin)).toEqual([0.9]);
  });

  // A status goes upstream under a stable `_id`, and Nightscout refuses one it already holds with a
  // duplicate-key error. That refusal means the status is there; counted as a failure, a few resends
  // would open the circuit breaker every sink shares, and nothing else would be written back.
  it("does not count a status Nightscout already holds against write-back", async () => {
    const device = `openaps://e2e-writeback-resend-${run}`;
    const at = new Date(Date.now() - 5 * MINUTE).toISOString();
    const status = { _id: objectId(), device, created_at: at, openaps: { iob: { iob: 0.3, timestamp: at } } };
    for (let i = 0; i < 6; i++) await tenant.api.ok("POST", "/api/v1/devicestatus", [status]);

    const readingDevice = `e2e-writeback-after-resend-${run}`;
    const reading = await createReading(readingDevice, 3);

    expect((await writtenBack("/api/v1/entries")).filter((e) => e.device === readingDevice).map((e) => e.identifier)).toEqual([reading.id]);
    expect((await upstreamRead("/api/v1/devicestatus.json", {})).filter((d) => d.device === device)).toHaveLength(1);
  });
});
