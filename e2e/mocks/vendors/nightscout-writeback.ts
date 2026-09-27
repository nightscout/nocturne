// A legacy Nightscout that keeps what it is sent, for a tenant whose Nightscout connector both
// writes back and pulls: entries, device statuses and treatments POSTed or PUT to it are served back
// on the next read, as a real instance would.
//
// Records are stored under the `_id` they arrive with, as Nightscout's upsert keeps a string
// `_id`; one arriving without an id gets a fresh ObjectId, as MongoDB would give it. State lives
// for the life of the mocks container and is shared by every tenant pointed here, so a spec keeps
// its records apart by device name.

import { NIGHTSCOUT_API_SECRET_HEADER } from "./nightscout.ts";
import type { Vendor, VendorReply, VendorRequest } from "./vendor.ts";

type Doc = Record<string, unknown> & { _id: string };

const entries = new Map<string, Doc>();
const deviceStatuses = new Map<string, Doc>();
const treatments = new Map<string, Doc>();
let minted = 0;

function newObjectId(): string {
  const seconds = Math.floor(Date.now() / 1000).toString(16).padStart(8, "0");
  return seconds + "e2e0" + (minted++).toString(16).padStart(12, "0");
}

function store(into: Map<string, Doc>, body: string): Doc[] {
  const parsed = JSON.parse(body) as Record<string, unknown> | Record<string, unknown>[];
  const docs = Array.isArray(parsed) ? parsed : [parsed];
  return docs.map((doc) => {
    const id = typeof doc._id === "string" && doc._id.length > 0 ? doc._id : newObjectId();
    const stored = { ...doc, _id: id };
    into.set(id, stored);
    return stored;
  });
}

function count(query: Record<string, string>): number {
  const n = Number(query.count ?? 10);
  return Number.isFinite(n) && n > 0 ? n : 10;
}

function readEntries(query: Record<string, string>, type?: string): Doc[] {
  const gte = query["find[date][$gte]"];
  const lte = query["find[date][$lte]"];
  return [...entries.values()]
    .filter((e) => type === undefined || e.type === type)
    .filter((e) => (gte === undefined || Number(e.date) >= Number(gte)) && (lte === undefined || Number(e.date) <= Number(lte)))
    .sort((a, b) => Number(b.date) - Number(a.date))
    .slice(0, count(query));
}

function readByCreatedAt(from: Map<string, Doc>, query: Record<string, string>): Doc[] {
  const gte = query["find[created_at][$gte]"];
  const lte = query["find[created_at][$lte]"];
  const id = query["find[_id]"];
  const clientId = query["find[id]"];
  const at = (d: Doc) => String(d.created_at ?? "");
  return [...from.values()]
    .filter((d) => (gte === undefined || at(d) >= gte) && (lte === undefined || at(d) <= lte))
    .filter((d) => (id === undefined || d._id === id) && (clientId === undefined || d.id === clientId))
    .sort((a, b) => (at(a) < at(b) ? 1 : at(a) > at(b) ? -1 : 0))
    .slice(0, count(query));
}

const ok = (body: unknown): VendorReply => ({ status: 200, body });

export const nightscoutWriteBack: Vendor = {
  handle(request: VendorRequest): VendorReply {
    if (request.path === "/api/v1/status.json") {
      return ok({ status: "ok", name: "nightscout", version: "15.0.2", apiEnabled: true, serverTimeEpoch: Date.now() });
    }
    if (request.headers["api-secret"]?.toLowerCase() !== NIGHTSCOUT_API_SECRET_HEADER) {
      return { status: 401, body: { status: 401, message: "Unauthorized" } };
    }

    const write = request.method === "POST" || request.method === "PUT";
    switch (request.path) {
      case "/api/v1/entries":
        return write ? ok(store(entries, request.body)) : { status: 405, body: "" };
      case "/api/v1/devicestatus":
        return write ? ok(store(deviceStatuses, request.body)) : { status: 405, body: "" };
      case "/api/v1/treatments":
        return write ? ok(store(treatments, request.body)) : { status: 405, body: "" };
      case "/api/v1/entries.json":
        return ok(readEntries(request.query));
      case "/api/v1/entries/sgv.json":
        return ok(readEntries(request.query, "sgv"));
      case "/api/v1/devicestatus.json":
        return ok(readByCreatedAt(deviceStatuses, request.query));
      case "/api/v1/treatments.json":
        return ok(readByCreatedAt(treatments, request.query));
      case "/api/v1/food.json":
      case "/api/v1/activity.json":
      case "/api/v1/profile.json":
        return ok([]);
      default:
        return { status: 404, body: { status: 404, message: `fake nightscout has no ${request.path}` } };
    }
  },
};
