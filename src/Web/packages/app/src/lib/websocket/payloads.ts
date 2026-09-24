import { z } from "zod";
import type { InAppNotificationDto } from "$lib/api/generated/nocturne-api-client";
import { isOneOf, isRecord, nonEmptyString } from "$lib/utils/type-guards";
import { isoNow } from "$lib/utils/now";
import type {
  AlarmEvent,
  AnnouncementEvent,
  Entry,
  StatusEvent,
  StorageEvent,
  SyncProgressEvent,
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

export function parseDataUpdate(data: unknown): Entry[] {
  return (Array.isArray(data) ? data : [data]).filter(isEntryDocument);
}

export function parseStorageEvent(data: unknown): StorageEvent {
  const fields = fieldsOf(data);
  return {
    colName: nonEmptyString(fields.colName) ?? nonEmptyString(fields.collection) ?? "entries",
    doc: fields.doc || fields.document || data,
  };
}

export function parseAnnouncement(data: unknown): AnnouncementEvent {
  const fields = fieldsOf(data);
  return {
    message: nonEmptyString(fields.message) ?? nonEmptyString(fields.text) ?? String(data),
    title: nonEmptyString(fields.title) ?? "Announcement",
    level: nonEmptyString(fields.level) ?? "info",
    timestamp: nonEmptyString(fields.timestamp) ?? isoNow(),
  };
}

export function parseAlarm(data: unknown): AlarmEvent {
  const fields = fieldsOf(data);
  return {
    level: nonEmptyString(fields.level) ?? "warn",
    title: nonEmptyString(fields.title) ?? "Alarm",
    message: nonEmptyString(fields.message),
    plugin: nonEmptyString(fields.plugin) ?? nonEmptyString(fields.source),
    timestamp: nonEmptyString(fields.timestamp) ?? isoNow(),
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
    timestamp: nonEmptyString(fields.timestamp),
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

const TRACKER_ACTIONS = [
  "create",
  "update",
  "delete",
  "complete",
  "ack",
] as const satisfies readonly TrackerUpdateEvent["action"][];

/** A tracker event needs both a known action and an identifiable instance, since
 *  the store keys every action on `instance.id`. */
export function parseTrackerUpdate(data: unknown): TrackerUpdateEvent | null {
  const fields = fieldsOf(data);
  if (!isOneOf(TRACKER_ACTIONS, fields.action)) return null;

  const instance = fields.instance;
  if (!isRecord(instance) || nonEmptyString(instance.id) === undefined) return null;

  return {
    action: fields.action,
    instance: instance as TrackerUpdateEvent["instance"],
  };
}

const SyncProgressSchema = z.object({
  connectorId: z.string(),
  connectorName: z.string().catch(""),
  phase: z.enum(["Syncing", "Completed", "Failed"]),
  errorMessage: z.string().nullable().catch(null),
  timestamp: z.string().catch(""),
  messageType: z
    .enum([
      "Authenticating",
      "FetchingData",
      "ProcessingDataType",
      "PublishingDataType",
      "SyncComplete",
      "SyncFailed",
    ])
    .nullable()
    .catch(null),
  messageParams: z.record(z.string(), z.string()).nullable().catch(null),
});

export function parseSyncProgress(data: unknown): SyncProgressEvent | null {
  const result = SyncProgressSchema.safeParse(data);
  return result.success ? result.data : null;
}
