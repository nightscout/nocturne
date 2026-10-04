// A legacy Nightscout that keeps what it is sent, for a tenant whose Nightscout connector both
// writes back and pulls: entries, device statuses and treatments POSTed or PUT to it are served back
// on the next read.
//
// Ids are normalised as cgm-remote-monitor 15.0.7 and later do (REQ-SYNC-072, UUID_HANDLING on),
// and it reports itself as 15.0.8:
// - a `_id` that is not a 24-hex ObjectId moves to `identifier` when that is empty and is dropped,
//   so the record gets a fresh ObjectId (entries and treatments); a 24-hex `_id` is kept, as an
//   ObjectId;
// - a treatment carrying an `identifier` is upserted by `$or: [{identifier}, {_id: identifier}]`
//   and its `_id` is dropped, so a new one gets a fresh ObjectId and an existing one keeps its own.
//   The `_id` arm compares a string, so it never matches a document stored under an ObjectId;
// - a treatment with no `identifier` is replaced by its `_id`, else by created_at and eventType;
// - an entry is upserted by date and type with `$set: doc`, in one ordered bulk write: a `_id`
//   other than the stored reading's is an immutable-field error that stores nothing from that
//   document on and fails the request;
// - device statuses: a `_id` that is not a 24-hex string is refused with 400; one that is is stored
//   as the string sent (no ObjectId cast, no case change). They are inserted in one ordered
//   `insertMany`: a `_id` already stored is a duplicate-key error (E11000) that stores nothing from
//   that document on and fails the request with Mongo's message as its description;
// - `identifier` is always kept as sent, and `find[identifier]` (or `find[identifier][$in][n]`)
//   reads by it;
// - a treatment `_id` is an ObjectId or a string, as Mongo types it: every 24-hex `_id` this mock
//   stores from a write is an ObjectId, any other is a string, and `POST /__seed/treatments` stores
//   documents verbatim, every `_id` as a string, as a Nightscout before 15.0.7 kept what a POST sent
//   (a 24-hex one included) and as a migration's original stays. `find[_id]=<v>` casts a 24-hex value
//   to an ObjectId, which never matches a string `_id`, reads a uuid by `identifier` or a string
//   `_id`, and any other value by a string `_id`; `find[_id][$in][n]` casts each 24-hex value and
//   compares any other as a string (`lib/server/query.js` `updateIdQuery`, 15.0.7+). 15.0.6 itself,
//   which casts no `$in` value and throws on a non-hex `find[_id]`, is covered by the unit model
//   (`FakeNightscoutTreatments`), not here;
// - a treatment or device-status find that names neither `_id`, `created_at` nor `dateString` only
//   sees `created_at` in the last four days (`lib/server/query.js` `enforceDateFilter`, `deltaAgo`
//   = TWO_DAYS * 2).
// State lives for the life of the mocks container and is shared by every tenant pointed here, so a
// spec keeps its records apart by device name or time.

import { NIGHTSCOUT_API_SECRET_HEADER } from "./nightscout.ts";
import type { Vendor, VendorReply, VendorRequest } from "./vendor.ts";

type Doc = Record<string, unknown> & { _id: string };

const entries = new Map<string, Doc>();
const deviceStatuses = new Map<string, Doc>();
// Keyed by `_id` and its Mongo type, since a string `_id` and an ObjectId of the same hex are two keys.
const treatments = new Map<string, Doc>();
const stringIdTreatments = new WeakSet<Doc>();
let minted = 0;

function newObjectId(): string {
  const seconds = Math.floor(Date.now() / 1000).toString(16).padStart(8, "0");
  return seconds + "e2e0" + (minted++).toString(16).padStart(12, "0");
}

const OBJECT_ID = /^[0-9a-fA-F]{24}$/;
const MINUTE_MS = 60_000;

function parseDocs(body: string): Record<string, unknown>[] {
  const parsed = JSON.parse(body) as Record<string, unknown> | Record<string, unknown>[];
  return Array.isArray(parsed) ? parsed : [parsed];
}

/** Moves a non-ObjectId `_id` to `identifier` and drops it, as normalizeEntryId/normalizeTreatmentId do. */
function normaliseId(doc: Record<string, unknown>): Record<string, unknown> {
  const out = { ...doc };
  if (typeof out._id === "string" && !OBJECT_ID.test(out._id)) {
    if (out.identifier === undefined || out.identifier === null || out.identifier === "") out.identifier = out._id;
    delete out._id;
  } else if (typeof out._id === "string") {
    out._id = out._id.toLowerCase();
  } else {
    delete out._id;
  }
  return out;
}

function upsert(into: Map<string, Doc>, doc: Record<string, unknown>, existing: Doc | undefined): Doc {
  const id = existing?._id ?? (typeof doc._id === "string" ? doc._id : newObjectId());
  const stored = { ...doc, _id: id } as Doc;
  into.set(id, stored);
  return stored;
}

function storeEntries(body: string): VendorReply {
  const stored: Doc[] = [];
  for (const raw of parseDocs(body)) {
    const doc = normaliseId(raw);
    const existing = [...entries.values()].find((e) => Number(e.date) === Number(doc.date) && e.type === doc.type);
    if (existing && typeof doc._id === "string" && doc._id !== existing._id) {
      return {
        status: 500,
        body: { status: 500, message: "Performing an update on the path '_id' would modify the immutable field '_id'" },
      };
    }
    stored.push(upsert(entries, { ...existing, ...doc }, existing));
  }
  return ok(stored);
}

const isStringId = (t: Doc) => !OBJECT_ID.test(t._id) || stringIdTreatments.has(t);
const treatmentKey = (id: string, asString: boolean) => `${asString || !OBJECT_ID.test(id) ? "s" : "o"}:${id}`;

/** The string-literal arm of the identifier `$or`, which an ObjectId `_id` never equals. */
const stringIdIs = (t: Doc, value: string) => isStringId(t) && t._id === value;
/** A cast `_id` query value, which only an ObjectId `_id` equals. */
const objectIdIs = (t: Doc, value: string) => !isStringId(t) && t._id === value.toLowerCase();

function storeTreatment(doc: Record<string, unknown>, existing: Doc | undefined): Doc {
  const asString = existing !== undefined && isStringId(existing);
  const id = existing?._id ?? (typeof doc._id === "string" ? doc._id : newObjectId());
  const stored = { ...doc, _id: id } as Doc;
  if (asString) stringIdTreatments.add(stored);
  treatments.set(treatmentKey(id, asString), stored);
  return stored;
}

function storeTreatments(body: string): Doc[] {
  return parseDocs(body).map((raw) => {
    const doc = normaliseId(raw);
    let existing: Doc | undefined;
    const identifier = doc.identifier;
    if (typeof identifier === "string" && identifier.length > 0) {
      delete doc._id;
      existing = [...treatments.values()].find((t) => t.identifier === identifier || stringIdIs(t, identifier));
    } else if (typeof doc._id === "string") {
      existing = treatments.get(treatmentKey(doc._id, false));
    } else {
      existing = [...treatments.values()].find((t) => t.created_at === doc.created_at && t.eventType === doc.eventType);
    }
    return storeTreatment(doc, existing);
  });
}

/** Stores documents as sent, every `_id` a string, as a Nightscout before 15.0.7 kept a POST's. */
function seedTreatments(body: string): Doc[] {
  return parseDocs(body).map((raw) => {
    const stored = { ...raw, _id: String(raw._id) } as Doc;
    stringIdTreatments.add(stored);
    treatments.set(treatmentKey(stored._id, true), stored);
    return stored;
  });
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** `updateIdQuery` at 15.0.7+ over the treatments' `_id` finds, or null when the query names no `_id`. */
function treatmentIdFilter(query: Record<string, string>): ((t: Doc) => boolean) | null {
  const id = query["find[_id]"];
  const idIn = Object.entries(query)
    .filter(([k]) => k.startsWith("find[_id][$in]"))
    .map(([, v]) => v);
  if (id === undefined && idIn.length === 0) return null;
  const leaf = (t: Doc, v: string) => (OBJECT_ID.test(v) ? objectIdIs(t, v) : stringIdIs(t, v));
  return (t) =>
    (id === undefined || (UUID.test(id) ? t.identifier === id || stringIdIs(t, id) : leaf(t, id))) &&
    (idIn.length === 0 || idIn.some((v) => leaf(t, v)));
}

function storeDeviceStatuses(body: string): VendorReply {
  const docs = parseDocs(body);
  const invalid = docs.find((d) => d._id !== undefined && d._id !== null && !(typeof d._id === "string" && OBJECT_ID.test(d._id)));
  if (invalid) {
    return {
      status: 400,
      body: {
        status: 400,
        message: "Invalid _id format",
        description: `Must be 24-character hex string or omit for auto-generation. Got: ${String(invalid._id)}`,
      },
    };
  }
  const stored: Doc[] = [];
  for (const raw of docs) {
    const doc: Record<string, unknown> = { ...raw };
    if (doc._id === undefined || doc._id === null) delete doc._id;
    if (typeof doc._id === "string" && deviceStatuses.has(doc._id)) {
      return {
        status: 500,
        body: {
          status: 500,
          message: "Mongo Error",
          description: `E11000 duplicate key error collection: nightscout.devicestatus index: _id_ dup key: { _id: "${doc._id}" }`,
        },
      };
    }
    stored.push(upsert(deviceStatuses, doc, undefined));
  }
  return ok(stored);
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

const FOUR_DAYS = 4 * 24 * 60 * MINUTE_MS;

function readByCreatedAt(from: Map<string, Doc>, query: Record<string, string>, idFilter?: ((d: Doc) => boolean) | null): Doc[] {
  const bounded = Object.keys(query).some(
    (k) => k.startsWith("find[_id]") || k.startsWith("find[created_at]") || k.startsWith("find[dateString]"),
  );
  const gte = query["find[created_at][$gte]"] ?? (bounded ? undefined : new Date(Date.now() - FOUR_DAYS).toISOString());
  const lte = query["find[created_at][$lte]"];
  const id = query["find[_id]"];
  const clientId = query["find[id]"];
  const identifiers = Object.entries(query)
    .filter(([k]) => k === "find[identifier]" || k.startsWith("find[identifier][$in]"))
    .map(([, v]) => v);
  const at = (d: Doc) => String(d.created_at ?? "");
  return [...from.values()]
    .filter((d) => (gte === undefined || at(d) >= gte) && (lte === undefined || at(d) <= lte))
    .filter((d) => (idFilter ? idFilter(d) : id === undefined || d._id === id || d._id === id.toLowerCase()) && (clientId === undefined || d.id === clientId))
    .filter((d) => identifiers.length === 0 || identifiers.includes(String(d.identifier)))
    .sort((a, b) => (at(a) < at(b) ? 1 : at(a) > at(b) ? -1 : 0))
    .slice(0, count(query));
}

const ok = (body: unknown): VendorReply => ({ status: 200, body });

export const nightscoutWriteBack: Vendor = {
  handle(request: VendorRequest): VendorReply {
    if (request.path === "/api/v1/status.json") {
      return ok({ status: "ok", name: "nightscout", version: "15.0.8", apiEnabled: true, serverTimeEpoch: Date.now() });
    }
    if (request.headers["api-secret"]?.toLowerCase() !== NIGHTSCOUT_API_SECRET_HEADER) {
      return { status: 401, body: { status: 401, message: "Unauthorized" } };
    }

    const write = request.method === "POST" || request.method === "PUT";
    switch (request.path) {
      case "/api/v1/entries":
        return write ? storeEntries(request.body) : { status: 405, body: "" };
      case "/api/v1/devicestatus":
        return write ? storeDeviceStatuses(request.body) : { status: 405, body: "" };
      case "/api/v1/treatments":
        return write ? ok(storeTreatments(request.body)) : { status: 405, body: "" };
      case "/api/v1/entries.json":
        return ok(readEntries(request.query));
      case "/api/v1/entries/sgv.json":
        return ok(readEntries(request.query, "sgv"));
      case "/api/v1/devicestatus.json":
        return ok(readByCreatedAt(deviceStatuses, request.query));
      case "/api/v1/treatments.json":
        return ok(readByCreatedAt(treatments, request.query, treatmentIdFilter(request.query)));
      case "/__seed/treatments":
        return request.method === "POST" ? ok(seedTreatments(request.body)) : { status: 405, body: "" };
      case "/api/v1/food.json":
      case "/api/v1/activity.json":
      case "/api/v1/profile.json":
        return ok([]);
      default:
        return { status: 404, body: { status: 404, message: `fake nightscout has no ${request.path}` } };
    }
  },
};
