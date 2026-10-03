import { randomUUID } from "node:crypto";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { minutesAgo, postEntries, postTreatments, sgvSeries } from "../helpers/data.ts";
import { HubConnection } from "../helpers/signalr.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface StorageEvent {
  colName?: string;
  identifier?: string | null;
  doc?: Record<string, unknown>;
}

interface V1Treatment {
  _id: string;
  notes?: string;
  insulin?: number;
}

interface V1Entry {
  _id: string;
  date: number;
}

const storage = (args: unknown[]) => (args[0] ?? {}) as StorageEvent;

// The API's DataHub, which the Socket.IO bridge relays to legacy clients: what it broadcasts is
// what every realtime client ends up with.
describe("realtime data hub", () => {
  let tenant: Tenant;
  let hub: HubConnection;

  beforeAll(async () => {
    tenant = await seedTenant();
    hub = await HubConnection.connect({ host: tenant.host, hub: "data", token: tenant.accessToken });
    const auth = await hub.invoke<{ success: boolean; read: boolean }>("Authorize", { client: "e2e", token: tenant.accessToken });
    expect(auth).toMatchObject({ success: true, read: true });
    const sub = await hub.invoke<{ success: boolean; collections: string[] }>("Subscribe", { collections: ["entries", "treatments"] });
    expect(sub.collections).toEqual(expect.arrayContaining(["entries", "treatments"]));
  });

  afterAll(() => hub?.close());

  it("pushes an uploaded entry as a create and a data update", async () => {
    const [entry] = sgvSeries({ count: 1, valueAt: () => 211, device: "e2e-realtime" });
    await postEntries(tenant.api, [entry!]);

    const [created] = await hub.waitFor("create", (a) => storage(a).colName === "entries" && storage(a).doc?.sgv === 211, {
      what: "the entry's create event",
    });
    expect(storage([created]).doc?.date).toBe(entry!.date);
    await hub.waitFor("dataUpdate", (a) => Array.isArray(a[0]) && a[0].some((r: { sgv?: number }) => r.sgv === 211), {
      what: "the entry's data update",
    });
  });

  it("pushes an entry deleted through v3 by the _id REST serves as a delete", async () => {
    const [entry] = sgvSeries({ count: 1, end: Date.now() - 60 * 60 * 1000, valueAt: () => 97, device: "e2e-realtime" });
    await postEntries(tenant.api, [entry!]);
    await hub.waitFor("create", (a) => storage(a).colName === "entries" && storage(a).doc?.date === entry!.date, {
      what: "the entry's create event",
    });
    const [rest] = await tenant.api.ok<V1Entry[]>("GET", `/api/v1/entries.json?find[date][$eq]=${entry!.date}`);
    expect(rest).toBeDefined();

    const del = await tenant.api.delete(`/api/v3/entries/${rest!._id}`);
    expect(del.status, del.text).toBeLessThan(300);
    const [deleted] = await hub.waitFor("delete", (a) => storage(a).colName === "entries" && storage(a).doc?.date === entry!.date, {
      what: "the entry's delete event",
    });
    expect(storage([deleted]).identifier).toBe(rest!._id);
    expect(await tenant.api.ok<V1Entry[]>("GET", `/api/v1/entries.json?find[date][$eq]=${entry!.date}`)).toEqual([]);
  });

  it("deletes an entry through v1 by the _id REST serves", async () => {
    const [entry] = sgvSeries({ count: 1, end: Date.now() - 90 * 60 * 1000, valueAt: () => 98, device: "e2e-realtime" });
    await postEntries(tenant.api, [entry!]);
    const [rest] = await tenant.api.ok<V1Entry[]>("GET", `/api/v1/entries.json?find[date][$eq]=${entry!.date}`);
    expect(rest).toBeDefined();

    const del = await tenant.api.request<{ n: number; deletedCount: number }>("DELETE", `/api/v1/entries/${rest!._id}`);
    expect(del.status, del.text).toBe(200);
    expect(del.body).toMatchObject({ n: 1, deletedCount: 1 });
    const [deleted] = await hub.waitFor("delete", (a) => storage(a).colName === "entries" && storage(a).doc?.date === entry!.date, {
      what: "the entry's delete event",
    });
    expect(storage([deleted]).identifier).toBe(rest!._id);
    expect(await tenant.api.ok<V1Entry[]>("GET", `/api/v1/entries.json?find[date][$eq]=${entry!.date}`)).toEqual([]);

    const again = await tenant.api.request<{ n: number; deletedCount: number }>("DELETE", `/api/v1/entries/${rest!._id}`);
    expect(again.status, again.text).toBe(200);
    expect(again.body).toMatchObject({ n: 0, deletedCount: 0 });
  });

  it("pushes a treatment create and delete", async () => {
    const notes = `e2e realtime ${Date.now()}`;
    await postTreatments(tenant.api, [{ eventType: "Note", created_at: minutesAgo(15), notes, enteredBy: "e2e" }]);
    await hub.waitFor("create", (a) => storage(a).colName === "treatments" && storage(a).doc?.notes === notes, {
      what: "the treatment's create event",
    });

    const rest = (await tenant.api.ok<V1Treatment[]>("GET", "/api/v1/treatments.json?count=20")).find((t) => t.notes === notes);
    expect(rest).toBeDefined();
    const del = await tenant.api.delete(`/api/v1/treatments/${rest!._id}`);
    expect(del.status).toBeLessThan(300);
    const [deleted] = await hub.waitFor("delete", (a) => storage(a).colName === "treatments" && storage(a).doc?.notes === notes, {
      what: "the treatment's delete event",
    });
    expect(storage([deleted]).identifier).toBe(rest!._id);
  });

  it("carries the REST _id on a treatment create", async () => {
    const notes = `e2e realtime id ${Date.now()}`;
    const response = await tenant.api.ok<V1Treatment[]>("POST", "/api/v1/treatments", [
      { eventType: "Note", created_at: minutesAgo(25), notes, enteredBy: "e2e" },
    ]);
    const [created] = await hub.waitFor("create", (a) => storage(a).colName === "treatments" && storage(a).doc?.notes === notes, {
      what: "the treatment's create event",
    });

    const rest = (await tenant.api.ok<V1Treatment[]>("GET", "/api/v1/treatments.json?count=20")).find((t) => t.notes === notes);
    expect(rest).toBeDefined();
    expect(storage([created]).doc?._id).toBe(rest!._id);
    expect(response.map((t) => t._id)).toEqual([rest!._id]);
  });

  it("carries the REST _id on a treatment update", async () => {
    const upload = { eventType: "Correction Bolus", insulin: 0.35, created_at: minutesAgo(45), enteredBy: "loop://e2e-iphone", syncIdentifier: randomUUID() };
    await tenant.api.ok("POST", "/api/v1/treatments", [upload]);
    const rest = (await tenant.api.ok<V1Treatment[]>("GET", "/api/v1/treatments.json?count=20")).find((t) => t.insulin === 0.35);
    expect(rest).toBeDefined();

    await tenant.api.ok("PUT", `/api/v1/treatments/${rest!._id}`, { ...upload, insulin: 0.4 });
    const [updated] = await hub.waitFor("update", (a) => storage(a).colName === "treatments" && storage(a).doc?.insulin === 0.4, {
      what: "the treatment's update event",
    });
    expect(storage([updated]).doc?._id).toBe(rest!._id);
  });

  it("pushes no create when an uploader re-sends a treatment the user deleted", async () => {
    const upload = [{ eventType: "Correction Bolus", insulin: 0.65, created_at: minutesAgo(35), enteredBy: "loop://e2e-iphone", syncIdentifier: randomUUID() }];
    const isThis = (a: unknown[]) => storage(a).colName === "treatments" && storage(a).doc?.insulin === 0.65;
    await tenant.api.ok("POST", "/api/v1/treatments", upload);
    await hub.waitFor("create", isThis, { what: "the treatment's create event" });

    const rest = (await tenant.api.ok<V1Treatment[]>("GET", "/api/v1/treatments.json?count=20")).find((t) => t.insulin === 0.65);
    expect(rest).toBeDefined();
    expect((await tenant.api.delete(`/api/v1/treatments/${rest!._id}`)).status).toBeLessThan(300);
    await hub.waitFor("delete", isThis, { what: "the treatment's delete event" });

    await tenant.api.request("POST", "/api/v1/treatments", upload);
    // Writes are broadcast before their request returns, so once a later write has arrived, a
    // create for the re-upload would have too.
    const sentinel = `e2e realtime sentinel ${Date.now()}`;
    await postTreatments(tenant.api, [{ eventType: "Note", created_at: minutesAgo(5), notes: sentinel, enteredBy: "e2e" }]);
    await hub.waitFor("create", (a) => storage(a).doc?.notes === sentinel, { what: "the later write's create event" });

    expect(hub.events.filter((e) => e.target === "create" && isThis(e.args))).toHaveLength(1);
  });

  it("delivers nothing written to another tenant", async () => {
    const other = await seedTenant();
    await postEntries(other.api, sgvSeries({ count: 1, valueAt: () => 199, device: "e2e-other-tenant" }));
    // Writes are broadcast before their request returns, so once this tenant's later write has
    // arrived, the other tenant's would have too.
    await postEntries(tenant.api, sgvSeries({ count: 1, end: Date.now() - 2 * 60 * 60 * 1000, valueAt: () => 212, device: "e2e-realtime" }));
    await hub.waitFor("create", (a) => storage(a).doc?.sgv === 212, { what: "this tenant's later create" });

    const leaked = hub.events.filter((e) => JSON.stringify(e.args).includes("e2e-other-tenant"));
    expect(leaked).toEqual([]);
  });
});

