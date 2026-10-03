import { randomBytes, randomUUID } from "node:crypto";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { eventually } from "../helpers/http.ts";
import { minutesAgo, postEntries, postTreatments, sgvSeries } from "../helpers/data.ts";
import { HubConnection } from "../helpers/signalr.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

type Doc = Record<string, unknown>;

interface StorageEvent {
  colName?: string;
  identifier?: string | null;
  doc?: Doc;
}

const OBJECT_ID = /^[0-9a-f]{24}$/;

const storage = (args: unknown[]) => (args[0] ?? {}) as StorageEvent;
const isEvent = (colName: string, match: (doc: Doc) => boolean) => (args: unknown[]) => {
  const event = storage(args);
  return event.colName === colName && !!event.doc && match(event.doc);
};

/** A v3 body is either the record itself or a `{ status, result }` envelope around it. */
function unwrap<T>(body: unknown): T {
  const envelope = body as { result?: unknown };
  return (envelope && typeof envelope === "object" && "result" in envelope ? envelope.result : body) as T;
}

/** Any key besides `_id`/`identifier` that spells the id: a second, unconverted copy of it. */
function duplicateIdKeys(doc: Doc): string[] {
  return Object.keys(doc).filter((k) => k.toLowerCase() === "id");
}

const objectId = () => randomBytes(12).toString("hex");

/** Pump state gives a status a snapshot the devicestatus reads list; uploader state alone does not. */
const pump = (clock: string) => ({ clock, reservoir: 123.4, battery: { percent: 70 } });

/**
 * Entries and devicestatus reach a client over REST (v1 `_id`, v3 `identifier`) and over the data
 * hub. A client pairs the two by id and deletes by the id it holds, so every surface must carry the
 * same 24-hex ObjectId for the same record.
 */
describe("one wire identifier for entries and devicestatus", () => {
  let tenant: Tenant;
  let hub: HubConnection;

  beforeAll(async () => {
    tenant = await seedTenant();
    hub = await HubConnection.connect({ host: tenant.host, hub: "data", token: tenant.accessToken });
    const auth = await hub.invoke<{ success: boolean }>("Authorize", { client: "e2e", token: tenant.accessToken });
    expect(auth).toMatchObject({ success: true });
    const sub = await hub.invoke<{ collections: string[] }>("Subscribe", { collections: ["entries", "devicestatus"] });
    expect(sub.collections).toEqual(expect.arrayContaining(["entries", "devicestatus"]));
  });

  afterAll(() => hub?.close());

  it("v1 entry uploaded without an _id: the create and delete events carry the REST _id", async () => {
    const [entry] = sgvSeries({ count: 1, end: Date.now() - 10 * 60_000, valueAt: () => 173, device: "e2e-wire-v1" });
    const posted = await tenant.api.ok<Doc[]>("POST", "/api/v1/entries", [entry!]);
    const id = posted[0]!._id as string;
    expect(id).toMatch(OBJECT_ID);

    const [created] = await hub.waitFor("create", isEvent("entries", (d) => d.date === entry!.date), { what: "the entry's create event" });
    expect(storage([created]).doc?._id).toBe(id);
    const listed = await tenant.api.ok<Doc[]>("GET", `/api/v1/entries.json?find[date][$eq]=${entry!.date}`);
    expect(listed.map((e) => e._id)).toEqual([id]);

    const del = await tenant.api.delete(`/api/v1/entries/${id}`);
    expect(del.status, del.text).toBeLessThan(300);
    const [deleted] = await hub.waitFor("delete", (a) => storage(a).colName === "entries" && (storage(a).identifier === id || storage(a).doc?.date === entry!.date), {
      what: "the entry's delete event",
    });
    expect(storage([deleted]).identifier).toBe(id);
  });

  it("v1 entry uploaded with an _id: the create, update and delete events carry it", async () => {
    const id = objectId();
    const [entry] = sgvSeries({ count: 1, end: Date.now() - 15 * 60_000, valueAt: () => 164, device: "e2e-wire-v1-keyed" });
    const posted = await tenant.api.ok<Doc[]>("POST", "/api/v1/entries", [{ ...entry!, _id: id }]);
    expect(posted[0]!._id).toBe(id);

    const [created] = await hub.waitFor("create", isEvent("entries", (d) => d.date === entry!.date), { what: "the entry's create event" });
    expect(storage([created]).doc?._id).toBe(id);
    expect(unwrap<Doc>(await tenant.api.ok("GET", `/api/v3/entries/${id}`)).identifier).toBe(id);

    const put = await tenant.api.request<Doc>("PUT", `/api/v1/entries/${id}`, { ...entry!, sgv: 181 });
    expect(put.status, put.text).toBe(200);
    expect(put.body._id).toBe(id);
    const [updated] = await hub.waitFor("update", isEvent("entries", (d) => d.date === entry!.date && d.sgv === 181), {
      what: "the entry's update event",
    });
    expect(storage([updated]).doc?._id).toBe(id);
    const listed = await tenant.api.ok<Doc[]>("GET", `/api/v1/entries.json?find[date][$eq]=${entry!.date}`);
    expect(listed.map((e) => [e._id, e.sgv])).toEqual([[id, 181]]);

    const del = await tenant.api.delete(`/api/v1/entries/${id}`);
    expect(del.status, del.text).toBeLessThan(300);
    const [deleted] = await hub.waitFor("delete", (a) => storage(a).colName === "entries" && (storage(a).identifier === id || storage(a).doc?.date === entry!.date), {
      what: "the entry's delete event",
    });
    expect(storage([deleted]).identifier).toBe(id);
    expect(await tenant.api.ok<Doc[]>("GET", `/api/v1/entries.json?find[date][$eq]=${entry!.date}`)).toEqual([]);
  });

  it("v3 entry uploaded without an identifier: the create, update and delete events carry the returned identifier", async () => {
    const date = Date.now() - 20 * 60_000;
    const body = { type: "sgv", sgv: 147, date, dateString: new Date(date).toISOString(), direction: "Flat", device: "e2e-wire-v3", app: "e2e" };
    const created = await tenant.api.post<unknown>("/api/v3/entries", body);
    expect(created.status, created.text).toBe(201);
    const id = unwrap<Doc>(created.body).identifier as string;
    expect(id, created.text).toMatch(OBJECT_ID);

    const [createEvent] = await hub.waitFor("create", isEvent("entries", (d) => d.date === date), { what: "the v3 entry's create event" });
    expect(storage([createEvent]).doc).toMatchObject({ _id: id, identifier: id });
    const listed = await tenant.api.ok<Doc[]>("GET", `/api/v1/entries.json?find[date][$eq]=${date}`);
    expect(listed.map((e) => e._id)).toEqual([id]);

    const put = await tenant.api.request("PUT", `/api/v3/entries/${id}`, { ...body, sgv: 152 });
    expect(put.status, put.text).toBe(200);
    expect(unwrap<Doc>(put.body).identifier).toBe(id);
    const [updateEvent] = await hub.waitFor("update", isEvent("entries", (d) => d.date === date && d.sgv === 152), {
      what: "the v3 entry's update event",
    });
    expect(storage([updateEvent]).doc?._id).toBe(id);

    const del = await tenant.api.delete(`/api/v3/entries/${id}`);
    expect(del.status, del.text).toBeLessThan(300);
    const [deleteEvent] = await hub.waitFor("delete", (a) => storage(a).colName === "entries" && (storage(a).identifier === id || storage(a).doc?.date === date), {
      what: "the v3 entry's delete event",
    });
    expect(storage([deleteEvent]).identifier).toBe(id);
  });

  it("v1 devicestatus uploaded without an _id: the create and delete events carry the REST _id", async () => {
    const device = `e2e-wire-v1ds-${objectId().slice(0, 6)}`;
    const posted = await tenant.api.ok<Doc[]>("POST", "/api/v1/devicestatus", [
      { device, created_at: minutesAgo(3), uploader: { battery: 57 }, pump: pump(minutesAgo(3)) },
    ]);
    const id = posted[0]!._id as string;
    expect(id).toMatch(OBJECT_ID);

    const [created] = await hub.waitFor("create", isEvent("devicestatus", (d) => d.device === device), { what: "the devicestatus create event" });
    expect(storage([created]).doc?._id).toBe(id);

    const listed = await tenant.api.ok<Doc[]>("GET", "/api/v1/devicestatus.json?count=50");
    expect(listed.filter((s) => s.device === device).map((s) => s._id)).toEqual([id]);
    expect(unwrap<Doc>(await tenant.api.ok("GET", `/api/v3/devicestatus/${id}`)).identifier).toBe(id);

    const del = await tenant.api.delete(`/api/v1/devicestatus/${id}`);
    expect(del.status).toBeLessThan(300);
    const [deleted] = await hub.waitFor("delete", (a) => storage(a).colName === "devicestatus" && storage(a).identifier === id, {
      what: "the devicestatus delete event",
    });
    expect(storage([deleted]).doc?._id).toBe(id);
    const after = await tenant.api.ok<Doc[]>("GET", "/api/v1/devicestatus.json?count=50");
    expect(after.filter((s) => s.device === device)).toEqual([]);
  });

  it("v3 devicestatus uploaded without an identifier: the create, update and delete events carry it", async () => {
    const device = `e2e-wire-v3ds-${objectId().slice(0, 6)}`;
    const created = await tenant.api.post<unknown>("/api/v3/devicestatus", { device, app: "e2e", created_at: minutesAgo(4), uploader: { battery: 61 }, pump: pump(minutesAgo(4)) });
    expect(created.status).toBe(201);
    const results = unwrap<Doc[]>(created.body);
    const id = results[0]!.identifier as string;
    expect(id).toMatch(OBJECT_ID);

    const [createEvent] = await hub.waitFor("create", isEvent("devicestatus", (d) => d.device === device), { what: "the v3 devicestatus create event" });
    expect(storage([createEvent]).doc?._id).toBe(id);
    expect(unwrap<Doc>(await tenant.api.ok("GET", `/api/v3/devicestatus/${id}`)).identifier).toBe(id);

    const put = await tenant.api.request("PUT", `/api/v3/devicestatus/${id}`, { device, app: "e2e", created_at: minutesAgo(4), uploader: { battery: 42 }, pump: pump(minutesAgo(4)) });
    expect(put.status).toBe(200);
    expect(unwrap<Doc>(put.body).identifier).toBe(id);
    const [updateEvent] = await hub.waitFor("update", isEvent("devicestatus", (d) => d.device === device), { what: "the v3 devicestatus update event" });
    expect(storage([updateEvent]).doc?._id).toBe(id);
    const listed = await tenant.api.ok<Doc[]>("GET", "/api/v1/devicestatus.json?count=50");
    expect(listed.filter((s) => s.device === device).map((s) => s._id)).toEqual([id]);

    const del = await tenant.api.delete(`/api/v3/devicestatus/${id}`);
    expect(del.status).toBeLessThan(300);
    const [deleteEvent] = await hub.waitFor("delete", (a) => storage(a).colName === "devicestatus" && storage(a).identifier === id, {
      what: "the v3 devicestatus delete event",
    });
    expect(storage([deleteEvent]).doc?._id).toBe(id);
    expect((await tenant.api.get(`/api/v3/devicestatus/${id}`)).status).toBe(404);
  });

  it("v3 PUT on a devicestatus stored without a legacy id replaces it in place under the identifier it had", async () => {
    // Snapshots written through v4 carry no legacy id, as connector statuses do.
    const device = `e2e-wire-keyless-${objectId().slice(0, 6)}`;
    const correlationId = randomUUID();
    const timestamp = minutesAgo(8);
    const pumps = await tenant.api.post<Doc[]>("/api/v4/device-status/pump", [
      { timestamp, device, correlationId, dataSource: "e2e-connector", syncIdentifier: `pump-${correlationId}`, manufacturer: "Tandem", model: "t:slim X2", reservoir: 120 },
    ]);
    expect(pumps.status, pumps.text).toBe(201);
    const pumpId = pumps.body[0]!.id as string;
    const uploaders = await tenant.api.post<Doc[]>("/api/v4/device-status/uploader", [{ timestamp, device, correlationId, battery: 80 }]);
    expect(uploaders.status, uploaders.text).toBe(201);

    const id = pumpId.replaceAll("-", "").slice(0, 24);
    expect(unwrap<Doc>(await tenant.api.ok("GET", `/api/v3/devicestatus/${id}`)).uploader).toBeDefined();

    const put = await tenant.api.request("PUT", `/api/v3/devicestatus/${id}`, {
      device,
      app: "e2e",
      created_at: timestamp,
      pump: { manufacturer: "Tandem", model: "t:slim X2", reservoir: 95 },
    });
    expect(put.status, put.text).toBe(200);
    const replaced = unwrap<Doc>(put.body);
    expect(replaced.identifier).toBe(id);
    expect(replaced.pump).toMatchObject({ reservoir: 95 });
    expect(replaced.uploader).toBeUndefined();

    const listed = await tenant.api.ok<Doc[]>("GET", "/api/v1/devicestatus.json?count=50");
    const mine = listed.filter((s) => s.device === device);
    expect(mine.map((s) => s._id)).toEqual([id]);
    expect(mine[0]!.pump).toMatchObject({ reservoir: 95 });
    expect(mine[0]!.uploader).toBeUndefined();

    const storedPumps = await tenant.api.ok<{ data: Doc[] }>("GET", `/api/v4/device-status/pump?device=${device}&limit=50`);
    expect(storedPumps.data.map((p) => [p.id, p.reservoir])).toEqual([[pumpId, 95]]);
    const storedUploaders = await tenant.api.ok<{ data: Doc[] }>("GET", `/api/v4/device-status/uploader?device=${device}&limit=50`);
    expect(storedUploaders.data).toEqual([]);
  });

  it("v3 DELETE on a devicestatus stored without a legacy id removes it by the identifier it is served under", async () => {
    const device = `e2e-wire-keyless-del-${objectId().slice(0, 6)}`;
    const correlationId = randomUUID();
    const timestamp = minutesAgo(9);
    const pumps = await tenant.api.post<Doc[]>("/api/v4/device-status/pump", [
      { timestamp, device, correlationId, dataSource: "e2e-connector", syncIdentifier: `pump-${correlationId}`, manufacturer: "Tandem", model: "t:slim X2", reservoir: 110 },
    ]);
    expect(pumps.status, pumps.text).toBe(201);
    const uploaders = await tenant.api.post<Doc[]>("/api/v4/device-status/uploader", [{ timestamp, device, correlationId, battery: 75 }]);
    expect(uploaders.status, uploaders.text).toBe(201);

    const before = await tenant.api.ok<Doc[]>("GET", "/api/v1/devicestatus.json?count=50");
    const served = before.filter((s) => s.device === device).map((s) => s._id as string);
    expect(served).toHaveLength(1);
    const id = served[0]!;
    expect(id).toMatch(OBJECT_ID);

    const del = await tenant.api.delete(`/api/v3/devicestatus/${id}`);
    expect(del.status, del.text).toBe(204);

    expect((await tenant.api.get(`/api/v3/devicestatus/${id}`)).status).toBe(404);
    const after = await tenant.api.ok<Doc[]>("GET", "/api/v1/devicestatus.json?count=50");
    expect(after.filter((s) => s.device === device)).toEqual([]);
    const storedPumps = await tenant.api.ok<{ data: Doc[] }>("GET", `/api/v4/device-status/pump?device=${device}&limit=50`);
    expect(storedPumps.data).toEqual([]);
    const storedUploaders = await tenant.api.ok<{ data: Doc[] }>("GET", `/api/v4/device-status/uploader?device=${device}&limit=50`);
    expect(storedUploaders.data).toEqual([]);
    expect((await tenant.api.delete(`/api/v3/devicestatus/${id}`)).status).toBe(404);
  });

  it("serializes no raw id beside _id on v1, v3 and the data hub", async () => {
    const device = `e2e-wire-shape-${objectId().slice(0, 6)}`;
    await postEntries(tenant.api, sgvSeries({ count: 2, end: Date.now() - 2 * 60 * 60_000, device }));
    await tenant.api.ok("POST", "/api/v1/devicestatus", [{ device, created_at: minutesAgo(5), uploader: { battery: 80 }, pump: pump(minutesAgo(5)) }]);
    await postTreatments(tenant.api, [{ eventType: "Note", created_at: minutesAgo(30), notes: `e2e wire ${device}`, enteredBy: "e2e" }]);

    const lists: Array<[string, (b: unknown) => Doc[]]> = [
      ["/api/v1/entries.json?count=10", (b) => b as Doc[]],
      ["/api/v1/devicestatus.json?count=10", (b) => b as Doc[]],
      ["/api/v1/treatments.json?count=10", (b) => b as Doc[]],
      ["/api/v3/entries?limit=10", (b) => unwrap<Doc[]>(b)],
      ["/api/v3/devicestatus?limit=10", (b) => unwrap<Doc[]>(b)],
      ["/api/v3/treatments?limit=10", (b) => unwrap<Doc[]>(b)],
    ];
    for (const [path, docs] of lists) {
      const res = await tenant.api.get(path);
      expect(res.status, path).toBe(200);
      const items = docs(res.body);
      expect(items.length, path).toBeGreaterThan(0);
      for (const item of items) {
        expect(duplicateIdKeys(item), `${path} ${JSON.stringify(item).slice(0, 200)}`).toEqual([]);
        expect(item._id ?? item.identifier, path).toMatch(OBJECT_ID);
      }
    }

    const [entryEvent] = await hub.waitFor("create", isEvent("entries", (d) => d.device === device), { what: "the shape spec's entry create" });
    const [statusEvent] = await hub.waitFor("create", isEvent("devicestatus", (d) => d.device === device), { what: "the shape spec's devicestatus create" });
    expect(duplicateIdKeys(storage([entryEvent]).doc!)).toEqual([]);
    expect(duplicateIdKeys(storage([statusEvent]).doc!)).toEqual([]);
  });

  it("keeps _id as identity when an uploaded entry also carries a different lowercase id", async () => {
    const _id = objectId();
    const [entry] = sgvSeries({ count: 1, end: Date.now() - 3 * 60 * 60_000, valueAt: () => 133, device: "e2e-wire-both-ids" });
    const posted = await tenant.api.ok<Doc[]>("POST", "/api/v1/entries", [{ ...entry!, _id, id: objectId() }]);
    expect(posted[0]!._id).toBe(_id);

    const [created] = await hub.waitFor("create", isEvent("entries", (d) => d.date === entry!.date), { what: "the entry's create event" });
    expect(storage([created]).doc?._id).toBe(_id);
    const listed = await tenant.api.ok<Doc[]>("GET", `/api/v1/entries.json?find[date][$eq]=${entry!.date}`);
    expect(listed.map((e) => e._id)).toEqual([_id]);
    expect(unwrap<Doc>(await tenant.api.ok("GET", `/api/v3/entries/${_id}`)).identifier).toBe(_id);
  });

  it("keeps _id as identity when a v3 devicestatus also carries a different lowercase id", async () => {
    const _id = objectId();
    const device = `e2e-wire-both-ids-${_id.slice(0, 6)}`;
    const created = await tenant.api.post<unknown>("/api/v3/devicestatus", {
      _id,
      id: objectId(),
      device,
      app: "e2e",
      created_at: minutesAgo(6),
      uploader: { battery: 33 },
      pump: pump(minutesAgo(6)),
    });
    expect(created.status).toBe(201);
    expect(unwrap<Doc[]>(created.body)[0]!.identifier).toBe(_id);

    const [event] = await hub.waitFor("create", isEvent("devicestatus", (d) => d.device === device), { what: "the devicestatus create event" });
    expect(storage([event]).doc?._id).toBe(_id);
    const fetched = await eventually(async () => {
      const res = await tenant.api.get(`/api/v3/devicestatus/${_id}`);
      return res.status === 200 ? unwrap<Doc>(res.body) : undefined;
    }, { timeoutMs: 10_000, what: "the devicestatus by its _id" });
    expect(fetched.identifier).toBe(_id);
  });

  it("deletes a body weight by the _id the list returns", async () => {
    const created = await tenant.api.post<Doc>("/api/v4/body-weight", { weightKg: 71.3, mills: Date.now() - 60_000 });
    expect(created.status).toBe(201);
    const listed = await tenant.api.ok<Doc[]>("GET", "/api/v4/body-weight?count=10");
    const row = listed.find((w) => w.weightKg === 71.3);
    expect(row).toBeDefined();
    expect(duplicateIdKeys(row!)).toEqual([]);
    expect(row!._id).toBe(created.body._id);

    const del = await tenant.api.delete(`/api/v4/body-weight/${row!._id}`);
    expect(del.status).toBeLessThan(300);
    const after = await tenant.api.ok<Doc[]>("GET", "/api/v4/body-weight?count=10");
    expect(after.find((w) => w._id === row!._id)).toBeUndefined();
  });
});
