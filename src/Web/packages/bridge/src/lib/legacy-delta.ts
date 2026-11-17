import { isRecord, stringField, type Payload } from './payload.js';

/**
 * The classic `dataUpdate` delta a Nightscout socket client expects once it has authorized:
 * `{ delta: true, sgvs?, mbgs?, cals?, treatments?, devicestatus?, profiles? }`, as upstream's
 * `lib/data/calcdelta.js` builds it. The API broadcasts one storage event per record instead, so
 * the bridge folds those into deltas here.
 *
 * LoopFollow reads a delta only for which keys it holds and refetches those collections over REST,
 * and it polls three times slower once the socket has announced itself (see `connected` in
 * `SocketIOServer`), so every live write has to put its key in a delta. A native V4 event carries no
 * v1 document: it puts its key in the delta with nothing under it.
 */

/** A v1 collection, which names the room a delta is delivered to. */
export type LegacyFamily = 'entries' | 'treatments' | 'devicestatus' | 'profiles';

type DeltaKey = 'sgvs' | 'mbgs' | 'cals' | 'treatments' | 'devicestatus' | 'profiles';

type StorageEventType = 'create' | 'update' | 'delete';

/**
 * Each API broadcast category's v1 collection. A V4 category answers to the same read scope as
 * the v1 collection it is listed with (`RealtimeCategories.GoverningScopes` in the API), so a
 * credential holds both or neither.
 */
const FAMILY_OF_CATEGORY: Record<string, LegacyFamily> = {
  entries: 'entries',
  glucose: 'entries',
  treatments: 'treatments',
  care: 'treatments',
  devicestatus: 'devicestatus',
  device: 'devicestatus',
  profiles: 'profiles',
  therapy: 'profiles',
};

const V1_COLLECTIONS: Record<string, true> = { entries: true, treatments: true, devicestatus: true, profiles: true };

const DEFAULT_KEY: Record<LegacyFamily, DeltaKey> = {
  entries: 'sgvs',
  treatments: 'treatments',
  devicestatus: 'devicestatus',
  profiles: 'profiles',
};

/** The V4 record types a v1 client reads from another collection than their category's. */
const V4_RECORD_KEY: Record<string, DeltaKey> = {
  meterGlucose: 'mbgs',
  calibration: 'cals',
  bgCheck: 'treatments',
  deviceEvent: 'treatments',
  stateSpan: 'treatments',
};

const ENTRY_TYPE_KEY: Record<string, DeltaKey> = { mbg: 'mbgs', cal: 'cals' };

/** The v1 collection a broadcast category belongs to, if a classic client reads it. */
export function legacyFamily(category: string): LegacyFamily | undefined {
  return FAMILY_OF_CATEGORY[category];
}

/** The v1 collections a socket granted `categories` receives deltas for. */
export function legacyFamilies(categories: readonly string[]): LegacyFamily[] {
  return [...new Set(categories.flatMap((category) => legacyFamily(category) ?? []))];
}

function millsOf(doc: Payload): number | undefined {
  for (const field of [doc.mills, doc.date]) {
    if (typeof field === 'number' && Number.isFinite(field)) return field;
  }
  const parsed = Date.parse(stringField(doc, 'created_at') ?? stringField(doc, 'dateString') ?? '');
  return Number.isFinite(parsed) ? parsed : undefined;
}

/** A v1 document as upstream's ddata holds it: `mills` on every record, `mgdl` on a reading. */
function ddataDocument(key: DeltaKey, doc: Payload): Payload {
  const mills = millsOf(doc);
  const mgdl = key === 'sgvs' ? doc.sgv : key === 'mbgs' ? doc.mbg : undefined;
  return {
    ...doc,
    ...(mills === undefined ? {} : { mills }),
    ...(mgdl === undefined ? {} : { mgdl }),
  };
}

interface DeltaEntry {
  family: LegacyFamily;
  key: DeltaKey;
  /** Absent when the event has nothing a v1 client can merge. */
  doc?: Payload;
}

/** Where a storage event lands in a delta, or null for a category no classic client reads. */
export function deltaEntry(eventType: StorageEventType, event: Payload): DeltaEntry | null {
  const category = stringField(event, 'colName') ?? stringField(event, 'collection');
  const family = category ? legacyFamily(category) : undefined;
  if (!category || !family) return null;

  if (!V1_COLLECTIONS[category]) {
    return { family, key: V4_RECORD_KEY[stringField(event, 'recordType') ?? ''] ?? DEFAULT_KEY[family] };
  }

  const doc = isRecord(event.doc) ? event.doc : undefined;
  const key = family === 'entries'
    ? ENTRY_TYPE_KEY[stringField(doc, 'type') ?? ''] ?? 'sgvs'
    : DEFAULT_KEY[family];

  if (eventType === 'delete') {
    // Upstream reports a removal for treatments alone.
    const id = stringField(doc, '_id') ?? stringField(event, 'identifier');
    return key === 'treatments' && id
      ? { family, key, doc: { _id: id, action: 'remove', ...(doc && millsOf(doc) !== undefined ? { mills: millsOf(doc) } : {}) } }
      : { family, key };
  }

  if (!doc) return { family, key };
  const document = ddataDocument(key, doc);
  return { family, key, doc: key === 'treatments' && eventType === 'update' ? { ...document, action: 'update' } : document };
}

/**
 * How long a tenant's storage events of one collection are gathered into one delta. One loop
 * cycle writes several records, and each delta makes a LoopFollow refetch.
 */
export const LEGACY_DELTA_WINDOW_MS = 1_000;

interface PendingDelta {
  tenantSlug: string;
  family: LegacyFamily;
  records: Partial<Record<DeltaKey, Payload[]>>;
  timer: ReturnType<typeof setTimeout>;
}

/** Gathers storage events into one classic delta per tenant and v1 collection. */
export class LegacyDeltaBuffer {
  private readonly pending = new Map<string, PendingDelta>();

  constructor(
    private readonly emit: (tenantSlug: string, family: LegacyFamily, delta: Payload) => void,
    private readonly windowMs: number = LEGACY_DELTA_WINDOW_MS,
  ) {}

  add(tenantSlug: string, eventType: StorageEventType, event: Payload): void {
    const entry = deltaEntry(eventType, event);
    if (!entry) return;

    const id = `${tenantSlug}\n${entry.family}`;
    let pending = this.pending.get(id);
    if (!pending) {
      const timer = setTimeout(() => this.flush(id), this.windowMs);
      timer.unref();
      pending = { tenantSlug, family: entry.family, records: {}, timer };
      this.pending.set(id, pending);
    }

    const records = (pending.records[entry.key] ??= []);
    if (entry.doc) records.push(entry.doc);
  }

  /** Drops what is gathered without emitting it. */
  clear(): void {
    for (const { timer } of this.pending.values()) clearTimeout(timer);
    this.pending.clear();
  }

  private flush(id: string): void {
    const pending = this.pending.get(id);
    if (!pending) return;
    this.pending.delete(id);

    const delta: Payload = { delta: true };
    for (const [key, records] of Object.entries(pending.records)) {
      delta[key] = records.sort((a, b) => Number(a.mills ?? 0) - Number(b.mills ?? 0));
    }
    this.emit(pending.tenantSlug, pending.family, delta);
  }
}
