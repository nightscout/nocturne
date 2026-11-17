import type { Entry } from "#lib/websocket/types.js";

/** Identifies an entry by its `_id`, or by its reading when it has none. */
export function entryIdentity(entry: Entry): string {
  return entry._id ? `id:${entry._id}` : entryReadingIdentity(entry);
}

export function entryReadingIdentity(entry: Entry): string {
  return `reading:${entry.mills ?? ""}:${entry.sgv ?? ""}`;
}

/**
 * Whether two entries are the same reading, by `_id` or by reading. An entry backfilled from the
 * V4 REST DTO carries the reading's uuid as `_id`, while a realtime event carries the legacy
 * ObjectId form, so the ids alone cannot pair the two copies.
 */
export function isSameEntry(a: Entry, b: Entry): boolean {
  if (typeof a._id === "string" && a._id === b._id) return true;
  return typeof a.mills === "number" && entryReadingIdentity(a) === entryReadingIdentity(b);
}

/**
 * Whether a held entry is the one `doc` names: by `_id` when an entry in `known` holds it,
 * else by {@link isSameEntry}. Two uploaders can report one reading under their own ids, so
 * the reading stands in for the id only when no entry holds it; matching it first lets one
 * uploader's update overwrite the other's row while its own row stays, which leaves two rows
 * under one `_id`.
 */
export function namedEntry(known: readonly Entry[], doc: Entry): (entry: Entry) => boolean {
  const id = doc._id;
  if (typeof id === "string" && known.some((entry) => entry._id === id)) {
    return (entry) => entry._id === id;
  }
  return (entry) => isSameEntry(entry, doc);
}

/**
 * The pending entries that match neither a known entry nor an earlier pending
 * one, by `_id` or by reading.
 */
export function unseenEntries(known: readonly Entry[], pending: readonly Entry[]): Entry[] {
  const ids = new Set(
    known.map((entry) => entry._id).filter((id): id is string => typeof id === "string")
  );
  const readings = new Set(known.map(entryReadingIdentity));
  return pending.filter((entry) => {
    const reading = entryReadingIdentity(entry);
    if ((typeof entry._id === "string" && ids.has(entry._id)) || readings.has(reading)) {
      return false;
    }
    if (typeof entry._id === "string") ids.add(entry._id);
    readings.add(reading);
    return true;
  });
}
