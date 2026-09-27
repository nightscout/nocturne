import { beforeAll, describe, expect, it } from "vitest";
import { env } from "../helpers/env.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";
import { NIGHTSCOUT_API_SECRET_HEADER } from "../../mocks/vendors/nightscout.ts";

// A tenant whose Nightscout connector writes back to the instance it pulls from
// (e2e/mocks/vendors/nightscout-writeback.ts keeps what it is sent, normalising ids as Nightscout
// 15.0.7+ does, and serves it back). Every record Nocturne writes upstream comes back on the next
// pull, often under a `_id` the upstream minted; it must land on the record it was written from,
// and a record the user deleted must stay deleted (#1804).
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

interface SyncResult {
  success: boolean;
  errors: string[];
}

/** The 24-hex prefix a uuid goes upstream under (MongoObjectId.FromGuid). */
function uuidPrefix(uuid: string): string {
  return uuid.replace(/[^0-9a-fA-F]/g, "").toLowerCase().slice(0, 24);
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

  beforeAll(async () => {
    tenant = await seedTenant();
    run = tenant.slug;
    await tenant.api.ok("PUT", "/api/v4/connectors/config/nightscout", {
      url: `${env.mocksUrlFromApi}/nightscout-writeback`,
      isActive: true,
      writeBackEnabled: true,
    });
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
    expect(sent.map((e) => [e._id, e.identifier])).toEqual([[uuidPrefix(reading.id), reading.id]]);

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
    expect(sent.map((e) => [e._id, e.identifier])).toEqual([[uuidPrefix(legacyId), legacyId]]);

    expect((await sync()).success).toBe(true);

    expect((await readings(device)).data).toHaveLength(1);
  });

  // A v4-native treatment has no legacy id: write-back sends it with its record's uuid prefix as
  // `_id` and its uuid as `identifier`, and Nightscout 15.0.7+ upserts it by that identifier under
  // a `_id` of its own. On main no path writes a v4-native treatment back before giving it a legacy
  // id, so the spec sends that payload upstream itself; the pull-back side is what is under test.
  async function createBolusWrittenBack(minutesAgo: number): Promise<{ bolus: Bolus; at: string }> {
    const at = new Date(Date.now() - minutesAgo * MINUTE).toISOString();
    const created = await tenant.api.post<Bolus>("/api/v4/insulin/boluses", { timestamp: at, insulin: 2.5 });
    expect(created.status).toBe(201);
    const upstream = await fetch(`${VENDOR}/api/v1/treatments`, {
      method: "POST",
      headers: { "content-type": "application/json", "api-secret": NIGHTSCOUT_API_SECRET_HEADER },
      body: JSON.stringify([
        { _id: uuidPrefix(created.body.id), identifier: created.body.id, eventType: "Correction Bolus", insulin: 2.5, created_at: at },
      ]),
    });
    expect(upstream.status).toBe(200);
    const [stored] = (await upstream.json()) as { _id: string }[];
    expect(stored!._id).not.toBe(uuidPrefix(created.body.id));
    return { bolus: created.body, at };
  }

  const bolusesAround = (at: string) => {
    const from = encodeURIComponent(new Date(Date.parse(at) - MINUTE).toISOString());
    const to = encodeURIComponent(new Date(Date.parse(at) + MINUTE).toISOString());
    return tenant.api.ok<Page<Bolus>>("GET", `/api/v4/insulin/boluses?limit=50&from=${from}&to=${to}`);
  };

  it("pulls a v4-native treatment written back under its uuid prefix onto that treatment", async () => {
    const { bolus, at } = await createBolusWrittenBack(40);

    expect((await sync()).success).toBe(true);

    expect((await bolusesAround(at)).data.map((b) => b.id)).toEqual([bolus.id]);
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
    const date = Date.now() - 12 * MINUTE;
    await tenant.api.ok("POST", "/api/v1/entries", [
      { _id: legacyId, type: "sgv", sgv: 133, date, dateString: new Date(date).toISOString(), device },
    ]);

    const [sent] = (await writtenBack("/api/v1/entries")).filter((e) => e.device === device);
    expect(sent!._id).toMatch(/^[0-9a-f]{24}$/);
    expect(sent!.identifier).toBe(legacyId);

    expect((await sync()).success).toBe(true);

    expect((await readings(device)).data).toHaveLength(1);
  });
});
