import { SyncPhase, TrackerRealtimeAction, type InAppNotificationDto } from "$lib/api/generated/nocturne-api-client";
import { isOneOf, isRecord, nonEmptyString } from "$lib/utils/type-guards";
import { isoNow } from "$lib/utils/now";
import { toIsoString } from "$lib/utils/api-date";
import type {
  AlarmEvent,
  AnnouncementEvent,
  Entry,
  StatusEvent,
  StorageEvent,
  SyncMessageType,
  SyncProgressEvent,
  TrackerDefinitionUpdateEvent,
  TrackerUpdateEvent,
} from "./types";

// Socket.IO hands listeners untyped JSON. These parsers are the only place a
// bridge payload is trusted with a type; each tolerates the same missing
// fields the listeners always defaulted.

const fieldsOf = (data: unknown): Record<string, unknown> =>
  isRecord(data) ? data : {};

/** An entry-shaped document: the fields the realtime store keys and sorts on,
 *  where present, carry their declared primitive types. */
export function isEntryDocument(value: unknown): value is Entry {
  return (
    isRecord(value) &&
    (value._id === undefined || value._id === null || typeof value._id === "string") &&
    (value.mills === undefined || value.mills === null || typeof value.mills === "number") &&
    (value.sgv === undefined || value.sgv === null || typeof value.sgv === "number")
  );
}

/** A native record document: the id the realtime store keys on is a string, and
 *  the mills it sorts on, where present, a number. */
export function isRecordDocument<T extends { id?: string; mills?: number }>(
  value: unknown
): value is T {
  return (
    isRecord(value) &&
    typeof value.id === "string" &&
    (value.mills === undefined || value.mills === null || typeof value.mills === "number")
  );
}

export function parseDataUpdate(data: unknown): Entry[] {
  return (Array.isArray(data) ? data : [data]).filter(isEntryDocument);
}

export function parseStorageEvent(data: unknown): StorageEvent {
  const fields = fieldsOf(data);
  return {
    colName: nonEmptyString(fields.colName) ?? nonEmptyString(fields.collection) ?? "entries",
    doc: fields.doc || fields.document || data,
    recordType: nonEmptyString(fields.recordType),
    id: nonEmptyString(fields.id),
  };
}

const NUMERIC_LEVELS: Readonly<Record<number, string>> = { 2: "urgent", 1: "warn", 0: "info" };

const levelOf = (value: unknown): string | undefined =>
  typeof value === "number" ? NUMERIC_LEVELS[value] : nonEmptyString(value);

const timestampOf = (value: unknown): string | undefined =>
  typeof value === "number" ? (toIsoString(new Date(value)) ?? undefined) : nonEmptyString(value);

export function parseAnnouncement(data: unknown): AnnouncementEvent {
  const fields = fieldsOf(data);
  return {
    message: nonEmptyString(fields.message) ?? nonEmptyString(fields.text) ?? String(data),
    title: nonEmptyString(fields.title) ?? "Announcement",
    level: levelOf(fields.level) ?? "info",
    timestamp: timestampOf(fields.timestamp) ?? isoNow(),
  };
}

export function parseAlarm(data: unknown): AlarmEvent {
  const fields = fieldsOf(data);
  return {
    level: levelOf(fields.level) ?? "warn",
    title: nonEmptyString(fields.title) ?? "Alarm",
    message: nonEmptyString(fields.message),
    plugin: nonEmptyString(fields.plugin) ?? nonEmptyString(fields.source),
    timestamp: timestampOf(fields.timestamp) ?? isoNow(),
    key: nonEmptyString(fields.key) ?? nonEmptyString(fields.id),
  };
}

/** Urgent alarms pass their fields through undefaulted, forced to `urgent`. */
export function parseUrgentAlarm(data: unknown): AlarmEvent {
  const fields = fieldsOf(data);
  return {
    title: nonEmptyString(fields.title),
    message: nonEmptyString(fields.message),
    plugin: nonEmptyString(fields.plugin),
    timestamp: timestampOf(fields.timestamp),
    key: nonEmptyString(fields.key),
    level: "urgent",
  };
}

export function parseStatus(data: unknown): StatusEvent {
  const fields = fieldsOf(data);
  return {
    status: nonEmptyString(fields.status) ?? nonEmptyString(fields.state) ?? "",
    message: nonEmptyString(fields.message),
    timestamp: nonEmptyString(fields.timestamp) ?? isoNow(),
  };
}

/** Dates stay ISO strings here, as they do on the REST path (the generated
 *  client parses with no reviver). */
export function parseNotification(data: unknown): InAppNotificationDto | null {
  return isRecord(data) && typeof data.id === "string" ? data : null;
}

/** A tracker event needs both a known action and an identifiable instance, since
 *  the store keys every action on `instance.id`. */
export function parseTrackerUpdate(data: unknown): TrackerUpdateEvent | null {
  const fields = fieldsOf(data);
  if (!isOneOf(Object.values(TrackerRealtimeAction), fields.action)) return null;

  const instance = fields.instance;
  if (!isRecord(instance) || nonEmptyString(instance.id) === undefined) return null;

  return {
    action: fields.action,
    instance,
  };
}

/** A definition event names the definition that was created, changed or deleted. */
export function parseTrackerDefinitionUpdate(data: unknown): TrackerDefinitionUpdateEvent | null {
  const definitionId = nonEmptyString(fieldsOf(data).definitionId);
  return definitionId === undefined ? null : { definitionId };
}

const SYNC_PHASES: readonly SyncPhase[] = Object.values(SyncPhase);

const SYNC_MESSAGE_TYPES = [
  "Authenticating",
  "FetchingData",
  "ProcessingDataType",
  "PublishingDataType",
  "SyncComplete",
  "SyncFailed",
] as const satisfies readonly SyncMessageType[];

const isStringRecord = (value: unknown): value is Record<string, string> =>
  isRecord(value) && Object.values(value).every((entry) => typeof entry === "string");

const stringOr = <T>(value: unknown, fallback: T): string | T =>
  typeof value === "string" ? value : fallback;

/** A sync event needs a connector and a known phase; every other field falls
 *  back to empty or null when missing or mistyped. */
export function parseSyncProgress(data: unknown): SyncProgressEvent | null {
  if (!isRecord(data) || typeof data.connectorId !== "string") return null;
  if (!isOneOf(SYNC_PHASES, data.phase)) return null;

  return {
    connectorId: data.connectorId,
    connectorName: stringOr(data.connectorName, ""),
    phase: data.phase,
    errorMessage: stringOr(data.errorMessage, null),
    timestamp: stringOr(data.timestamp, ""),
    messageType: isOneOf(SYNC_MESSAGE_TYPES, data.messageType) ? data.messageType : null,
    messageParams: isStringRecord(data.messageParams) ? { ...data.messageParams } : null,
  };
}
