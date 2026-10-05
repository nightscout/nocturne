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
  query: Record<string, string>;
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

interface TempBasal {
  id: string;
}

interface V1Treatment {
  _id: string;
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
  duration?: number;
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

/**
 * Stores treatments straight in the fake Nightscout as they are, every `_id` a string: what a
 * Nightscout before 15.0.7 kept of a POST, and what a migration's original stays after an upgrade.
 */
async function upstreamSeed(docs: Record<string, unknown>[]): Promise<void> {
  const response = await fetch(`${VENDOR}/__seed/treatments`, { method: "POST", headers: upstreamHeaders, body: JSON.stringify(docs) });
  expect(response.status).toBe(200);
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

/** The forms each `find[identifier][$in]` lookup of the treatments upstream named, in order. */
async function identifierLookups(): Promise<string[][]> {
  const requests = (await (await fetch(`${VENDOR}/__requests`)).json()) as VendorRequest[];
  return requests
    .filter((r) => r.method === "GET" && r.path === "/api/v1/treatments.json" && "find[identifier][$in][0]" in r.query)
    .map((r) =>
      Object.entries(r.query)
        .filter(([k]) => k.startsWith("find[identifier][$in]["))
        .sort(([a], [b]) => Number(a.match(/\d+/)![0]) - Number(b.match(/\d+/)![0]))
        .map(([, v]) => v),
    );
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

  const tempBasalsAround = (at: string) => {
    const from = encodeURIComponent(new Date(Date.parse(at) - MINUTE).toISOString());
    const to = encodeURIComponent(new Date(Date.parse(at) + MINUTE).toISOString());
    return tenant.api.ok<Page<TempBasal>>("GET", `/api/v4/insulin/temp-basals?limit=50&from=${from}&to=${to}`);
  };

  /** The `[_id, identifier]` of each write-back of the treatment its legacy key names, in order. */
  const sentTreatments = async (legacyKey: string) =>
    (await writtenBack("/api/v1/treatments")).filter((t) => t.identifier === wireId(legacyKey)).map((t) => [t._id, t.identifier]);

  // A genuinely new create goes out unprobed under the coerced legacy key, as `_id` and `identifier`
  // both; the upstream keeps that identifier under an ObjectId it mints. A PUT, a PATCH by the id the
  // create answered and a re-upload of the same treatment each look the copy up by every form a
  // release sent (the record's full uuid included) and are PUT under its ObjectId with the same
  // identifier, so each lands on the one copy upstream and each pull lands on the stored treatment.
  it("pulls a v1 treatment it wrote back on create, PUT, PATCH and re-upload onto that treatment", async () => {
    const syncIdentifier = crypto.randomUUID();
    const wire = wireId(syncIdentifier);
    const at = new Date(Date.now() - 55 * MINUTE).toISOString();
    const upload = { eventType: "Correction Bolus", insulin: 0.75, created_at: at, enteredBy: `loop://e2e-writeback-${run}`, syncIdentifier };
    const [created] = await tenant.api.ok<V1Treatment[]>("POST", "/api/v1/treatments", [upload]);
    const id = created!._id;
    expect(id).toMatch(/^[0-9a-f]{24}$/);
    expect(await sentTreatments(syncIdentifier)).toEqual([[wire, wire]]);
    const lookups = async () => (await identifierLookups()).filter((forms) => forms.includes(wire));
    expect(await lookups()).toEqual([]);
    const [copy] = await upstreamTreatmentsAt(at, "Correction Bolus");
    expect(copy?.identifier).toBe(wire);

    expect((await sync()).success).toBe(true);
    const stored = (await bolusesAround(at)).data;
    expect(stored).toHaveLength(1);
    expect(uuidPrefix(stored[0]!.id)).toBe(id);
    // The pulled copy is the write-back's echo: it leaves the upload's source, so later edits are written back too.
    const kept = [[stored[0]!.id, stored[0]!.dataSource ?? null]];
    expect(kept[0]![1]).not.toBe("nightscout-connector");

    await tenant.api.ok("PUT", `/api/v1/treatments/${id}`, { ...upload, insulin: 0.8 });
    expect(await sentTreatments(syncIdentifier)).toEqual([[wire, wire], [copy!._id, wire]]);
    const forms = [wire, id, syncIdentifier, stored[0]!.id.toLowerCase()];
    expect(await lookups()).toEqual([forms]);
    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[copy!._id, wire, 0.8]]);
    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => [b.id, b.dataSource ?? null])).toEqual(kept);

    await tenant.api.ok("PATCH", `/api/v3/treatments/${id}`, { insulin: 0.85 });
    expect(await sentTreatments(syncIdentifier)).toEqual([[wire, wire], [copy!._id, wire], [copy!._id, wire]]);
    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[copy!._id, wire, 0.85]]);
    expect(await lookups()).toEqual([forms, forms]);
    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => [b.id, b.dataSource ?? null])).toEqual(kept);

    const [resent] = await tenant.api.ok<V1Treatment[]>("POST", "/api/v1/treatments", [{ ...upload, insulin: 0.9 }]);
    expect(resent!._id).toBe(id);
    expect(await sentTreatments(syncIdentifier)).toEqual([[wire, wire], [copy!._id, wire], [copy!._id, wire], [copy!._id, wire]]);
    expect(await lookups()).toEqual([forms, forms, forms]);
    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[copy!._id, wire, 0.9]]);
    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => [b.id, b.dataSource ?? null])).toEqual(kept);
  });

  it("pulls a temp basal it wrote back onto that temp basal", async () => {
    const at = new Date(Date.now() - 60 * MINUTE).toISOString();
    const [created] = await tenant.api.ok<V1Treatment[]>("POST", "/api/v1/treatments", [
      { eventType: "Temp Basal", duration: 30, absolute: 0.45, rate: 0.45, created_at: at, enteredBy: `e2e-writeback-${run}` },
    ]);
    const id = created!._id;
    expect(id).toMatch(/^[0-9a-f]{24}$/);

    const sent = (await writtenBack("/api/v1/treatments")).filter((t) => t.created_at === at && t.eventType === "Temp Basal");
    expect(sent).toHaveLength(1);
    expect(sent[0]!._id).toMatch(/^[0-9a-f]{24}$/);
    expect(sent[0]!.identifier).toBe(sent[0]!._id);
    expect((await upstreamTreatmentsAt(at, "Temp Basal")).map((t) => t.identifier)).toEqual([sent[0]!.identifier]);

    expect((await sync()).success).toBe(true);

    const stored = (await tempBasalsAround(at)).data;
    expect(stored).toHaveLength(1);
    expect(uuidPrefix(stored[0]!.id)).toBe(id);
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

  // Nightscout narrows a treatment find that bounds no created_at to the last four days, so the
  // lookup an edit makes for its copy must bound created_at itself: missing the copy of an older
  // treatment, the edit would go out under its `_id` alone and 15.0.8 would store a second copy.
  it.each([
    ["an ObjectId", 6, () => objectId()],
    ["a uuid", 9, () => crypto.randomUUID()],
    ["another", 12, () => `e2e-tr-old-${crypto.randomUUID()}`],
  ])("writes an edit of a treatment keyed by %s and %i days old onto its one copy", async (_shape, daysAgo, key) => {
    const legacyId = key();
    const wire = wireId(legacyId);
    const at = new Date(Date.now() - daysAgo * 24 * 60 * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Correction Bolus", insulin: 0.4, created_at: at, enteredBy: `e2e-writeback-old-${run}` };
    await writeBack(false);
    try {
      await tenant.api.ok("POST", "/api/v1/treatments", [upload]);
    } finally {
      await writeBack(true);
    }
    const [copy] = await upstreamPost("/api/v1/treatments", [{ ...upload, _id: wire, identifier: wire }]);
    expect(await upstreamRead("/api/v1/treatments.json", { "find[identifier]": wire })).toEqual([]);

    const [bolus] = (await bolusesAround(at)).data;
    await tenant.api.ok("PUT", `/api/v1/treatments/${uuidPrefix(bolus!.id)}`, { ...upload, insulin: 0.8 });

    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[copy!._id, wire, 0.8]]);
    expect((await bolusesAround(at)).data.map((b) => [b.id, b.insulin])).toEqual([[bolus!.id, 0.8]]);
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

    const sentBefore = (await writtenBack("/api/v1/treatments")).length;
    await tenant.api.ok("PUT", `/api/v1/treatments/${legacyId}`, { ...upload, insulin: 0.9 });

    const sent = (await writtenBack("/api/v1/treatments")).slice(sentBefore).filter((t) => t._id === legacyId);
    expect(sent.map((t) => t.identifier)).toEqual([undefined]);
    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[legacyId, undefined, 0.9]]);

    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => b.insulin)).toEqual([0.9]);
  });

  /** Stores a v1 upload with write-back off, so that only what the spec puts upstream is there. */
  async function uploadWithoutWriteBack(upload: Record<string, unknown>): Promise<void> {
    await writeBack(false);
    try {
      await tenant.api.ok("POST", "/api/v1/treatments", [upload]);
    } finally {
      await writeBack(true);
    }
  }

  // Released versions left a treatment's copy under identifiers this release does not create under:
  // v0.0.1 to v0.2.3 sent the raw key, v0.2.4 to v0.2.7 sent a temp basal whose legacy id is not an
  // ObjectId under its record's uuid prefix, and main after #1960 sent every create so. An edit made
  // in Nocturne must find that copy and land on it, keeping its identifier: a second copy would give
  // followers, and an AAPS syncing from that Nightscout, the basal or the dose twice.
  it.each([
    ["v0.2.3 (the raw key)", 130, "raw"],
    ["v0.2.7 (the record's uuid prefix)", 140, "prefix"],
    ["this release (the coerced key)", 150, "coerced"],
  ])("writes an edit of a temp basal onto the copy %s left", async (_release, minutesAgo, form) => {
    const legacyId = `e2e-tb-${crypto.randomUUID()}`;
    const at = new Date(Date.now() - minutesAgo * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Temp Basal", duration: 30, absolute: 1.2, rate: 1.2, created_at: at, enteredBy: `e2e-writeback-tb-${run}` };
    await uploadWithoutWriteBack(upload);
    const [tempBasal] = (await tempBasalsAround(at)).data;
    expect(tempBasal).toBeDefined();
    const sentUnder = form === "raw" ? legacyId : form === "prefix" ? uuidPrefix(tempBasal!.id) : wireId(legacyId);
    const [copy] = await upstreamPost("/api/v1/treatments", [{ ...upload, _id: sentUnder, identifier: sentUnder }]);

    await tenant.api.ok("PUT", `/api/v1/treatments/${uuidPrefix(tempBasal!.id)}`, { ...upload, duration: 12 });

    expect((await upstreamTreatmentsAt(at, "Temp Basal")).map((t) => [t._id, t.identifier, t.duration])).toEqual([[copy!._id, sentUnder, 12]]);
    expect((await sync()).success).toBe(true);
    expect((await tempBasalsAround(at)).data.map((t) => t.id)).toEqual([tempBasal!.id]);
    expect(await upstreamTreatmentsAt(at, "Temp Basal")).toHaveLength(1);
  });

  it("writes a temp basal back on its upload and an edit of it onto that one copy", async () => {
    const legacyId = objectId();
    const at = new Date(Date.now() - 160 * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Temp Basal", duration: 30, absolute: 0.8, rate: 0.8, created_at: at, enteredBy: `e2e-writeback-tb-live-${run}` };
    await tenant.api.ok("POST", "/api/v1/treatments", [upload]);
    expect((await upstreamTreatmentsAt(at, "Temp Basal")).map((t) => [t.identifier, t.duration])).toEqual([[legacyId, 30]]);

    await tenant.api.ok("PUT", `/api/v1/treatments/${legacyId}`, { ...upload, duration: 10 });

    expect((await upstreamTreatmentsAt(at, "Temp Basal")).map((t) => [t.identifier, t.duration])).toEqual([[legacyId, 10]]);
    expect((await sync()).success).toBe(true);
    expect((await tempBasalsAround(at)).data).toHaveLength(1);
  });

  it.each([
    ["v0.2.3 (its raw key)", 170, "raw"],
    ["main after #1960 (its record's uuid prefix)", 180, "prefix"],
  ])("writes an edit of a bolus onto the copy %s left", async (_release, minutesAgo, form) => {
    const legacyId = `e2e-tr-${crypto.randomUUID()}`;
    const at = new Date(Date.now() - minutesAgo * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Correction Bolus", insulin: 0.5, created_at: at, enteredBy: `e2e-writeback-release-${run}` };
    await uploadWithoutWriteBack(upload);
    const [bolus] = (await bolusesAround(at)).data;
    const sentUnder = form === "raw" ? legacyId : uuidPrefix(bolus!.id);
    const [copy] = await upstreamPost("/api/v1/treatments", [{ ...upload, _id: sentUnder, identifier: sentUnder }]);

    await tenant.api.ok("PUT", `/api/v1/treatments/${uuidPrefix(bolus!.id)}`, { ...upload, insulin: 0.9 });

    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[copy!._id, sentUnder, 0.9]]);
    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => [b.id, b.insulin])).toEqual([[bolus!.id, 0.9]]);
  });

  // An edit of a treatment upstream holds nowhere goes up as a create does, under both keys. Under
  // its `_id` alone, 15.0.8 would hold it under that ObjectId with no identifier, and the next upload
  // of the treatment, upserted by identifier, would store a second copy.
  it("leaves one copy of a treatment edited while upstream held none, then uploaded again", async () => {
    const legacyId = objectId();
    const at = new Date(Date.now() - 190 * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Correction Bolus", insulin: 0.3, created_at: at, enteredBy: `e2e-writeback-nowhere-${run}` };
    await uploadWithoutWriteBack(upload);
    expect(await upstreamTreatmentsAt(at, "Correction Bolus")).toEqual([]);

    await tenant.api.ok("PUT", `/api/v1/treatments/${legacyId}`, { ...upload, insulin: 0.6 });
    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t.identifier, t.insulin])).toEqual([[legacyId, 0.6]]);

    await tenant.api.ok("POST", "/api/v1/treatments", [{ ...upload, insulin: 0.6 }]);

    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t.identifier, t.insulin])).toEqual([[legacyId, 0.6]]);
    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => b.insulin)).toEqual([0.6]);
  });

  // An upload of a treatment Nocturne already stores updates it, and goes upstream as the edit it
  // is: looked for first. Sent as a create, under both keys, 15.0.8 would match neither the original
  // it holds under its ObjectId with no identifier nor a copy under another form, and store a second.
  it("writes a re-upload of a treatment upstream holds under its ObjectId alone onto that treatment", async () => {
    const legacyId = objectId();
    const at = new Date(Date.now() - 210 * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Correction Bolus", insulin: 0.4, created_at: at, enteredBy: `e2e-writeback-reupload-${run}` };
    await upstreamPost("/api/v1/treatments", [upload]);
    await uploadWithoutWriteBack(upload);

    await tenant.api.ok("POST", "/api/v1/treatments", [{ ...upload, insulin: 0.8 }]);

    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[legacyId, undefined, 0.8]]);
    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => b.insulin)).toEqual([0.8]);
  });

  // The original of a treatment a Nightscout migration imported from an uploader that sent its own
  // id is held under that id as a string `_id`, with no identifier. An edit sent under the create's
  // keys matched neither arm of 15.0.8's identifier `$or` and stored a second copy.
  it("writes an edit of a treatment upstream holds under a string _id alone onto that treatment", async () => {
    const legacyId = `e2e-imported-${crypto.randomUUID()}`;
    const at = new Date(Date.now() - 220 * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Correction Bolus", insulin: 0.4, created_at: at, enteredBy: `e2e-writeback-imported-${run}` };
    await upstreamSeed([upload]);
    await uploadWithoutWriteBack(upload);
    const [bolus] = (await bolusesAround(at)).data;

    await tenant.api.ok("PUT", `/api/v1/treatments/${uuidPrefix(bolus!.id)}`, { ...upload, insulin: 0.7 });

    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[legacyId, legacyId, 0.7]]);
    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => [b.id, b.insulin])).toEqual([[bolus!.id, 0.7]]);
  });

  // A Nightscout that was 15.0.6 when write-back POSTed the copy holds it under its 24-hex key as a
  // string, which `find[_id]` casts past. The edit goes onto it by its identifier.
  it("writes an edit onto a copy held under its 24-hex key as a string since before 15.0.7", async () => {
    const legacyId = objectId();
    const at = new Date(Date.now() - 230 * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Correction Bolus", insulin: 0.4, created_at: at, enteredBy: `e2e-writeback-string-hex-${run}` };
    await uploadWithoutWriteBack(upload);
    await upstreamSeed([{ ...upload, identifier: legacyId }]);
    expect(await upstreamRead("/api/v1/treatments.json", { "find[_id]": legacyId })).toEqual([]);

    await tenant.api.ok("PUT", `/api/v1/treatments/${legacyId}`, { ...upload, insulin: 0.6 });

    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[legacyId, legacyId, 0.6]]);
    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => b.insulin)).toEqual([0.6]);
  });

  // Up to v0.2.3 an edit went upstream under the record's full uuid, whatever its legacy id, and
  // 15.0.7+ kept that uuid as the identifier of a copy under a minted ObjectId. A later edit lands
  // on that copy, and the copy pulled back lands on the treatment rather than beside it.
  it("writes an edit onto the copy a v0.2.3 edit left under the record's uuid and pulls it back onto the treatment", async () => {
    const legacyId = objectId();
    const at = new Date(Date.now() - 240 * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Correction Bolus", insulin: 0.4, created_at: at, enteredBy: `e2e-writeback-v023-edit-${run}` };
    await uploadWithoutWriteBack(upload);
    const [bolus] = (await bolusesAround(at)).data;
    const [copy] = await upstreamPost("/api/v1/treatments", [{ ...upload, _id: bolus!.id, identifier: bolus!.id }]);
    expect(copy!._id).not.toBe(bolus!.id);

    await tenant.api.ok("PUT", `/api/v1/treatments/${legacyId}`, { ...upload, insulin: 0.9 });

    expect((await upstreamTreatmentsAt(at, "Correction Bolus")).map((t) => [t._id, t.identifier, t.insulin])).toEqual([[copy!._id, bolus!.id, 0.9]]);
    expect((await sync()).success).toBe(true);
    expect((await bolusesAround(at)).data.map((b) => [b.id, b.insulin])).toEqual([[bolus!.id, 0.9]]);
  });

  it("does not write back an upload of a treatment the user deleted", async () => {
    const legacyId = objectId();
    const at = new Date(Date.now() - 200 * MINUTE).toISOString();
    const upload = { _id: legacyId, eventType: "Correction Bolus", insulin: 0.2, created_at: at, enteredBy: `e2e-writeback-deleted-tr-${run}` };
    await uploadWithoutWriteBack(upload);
    expect((await tenant.api.delete(`/api/v1/treatments/${legacyId}`)).status).toBeLessThan(300);

    await tenant.api.ok("POST", "/api/v1/treatments", [upload]);

    expect((await writtenBack("/api/v1/treatments")).filter((t) => t.identifier === legacyId || t._id === legacyId)).toEqual([]);
    expect(await upstreamTreatmentsAt(at, "Correction Bolus")).toEqual([]);
    expect((await bolusesAround(at)).data).toEqual([]);
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
