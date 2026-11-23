import type { HeldFact } from "@nocturne/api-client";
import type { AlertDispatchEvent, AlertPayload, MetPoint } from "../types.js";

const field = (source: object, name: string): unknown => (name in source ? Reflect.get(source, name) : undefined);

const asString = (value: unknown): string | undefined => (typeof value === "string" ? value : undefined);
const asNumber = (value: unknown): number | undefined => (typeof value === "number" ? value : undefined);

/** The pairs that are objects; a field of another type is left out, and the card shows it raw. */
const asHeldFacts = (value: unknown): HeldFact[] | undefined =>
  Array.isArray(value)
    ? value
        .filter((f): f is object => typeof f === "object" && f !== null)
        .map((f) => ({ fact: asString(field(f, "fact")), reason: asString(field(f, "reason")) }))
    : undefined;

/** The entries that are objects, each field read as its type or left out. */
const asMetBy = (value: unknown): MetPoint[] | undefined =>
  Array.isArray(value)
    ? value
        .filter((p): p is object => typeof p === "object" && p !== null)
        .map((p) => ({
          leafId: asNumber(field(p, "leafId")),
          kind: asString(field(p, "kind")),
          curve: asString(field(p, "curve")),
          offsetMinutes: asNumber(field(p, "offsetMinutes")),
          value: asNumber(field(p, "value")),
          limit: asNumber(field(p, "limit")),
          limitFact: asString(field(p, "limitFact")),
          limitOffset: asNumber(field(p, "limitOffset")),
        }))
    : undefined;

/**
 * Reads the payload without judging its values: a chat alert that is refused is dropped, so only
 * the ids the acknowledge action needs stop delivery. Any other value that is absent or of
 * another type gives a plainer card, and the cards degrade an enum member they do not know.
 */
function parseAlertPayload(raw: unknown): AlertPayload | undefined {
  if (typeof raw !== "object" || raw === null) return undefined;
  const excursionId = asString(field(raw, "excursionId"));
  const tenantId = asString(field(raw, "tenantId"));
  if (excursionId === undefined || tenantId === undefined) return undefined;
  return {
    ...raw,
    ruleName: asString(field(raw, "ruleName")) ?? "",
    subjectName: asString(field(raw, "subjectName")) ?? "",
    readingTimestamp: asString(field(raw, "readingTimestamp")) ?? "",
    excursionId,
    tenantId,
    severity: asString(field(raw, "severity")) ?? "",
    kind: asString(field(raw, "kind")),
    alertType: asString(field(raw, "alertType")),
    glucoseValue: asNumber(field(raw, "glucoseValue")),
    trendRate: asNumber(field(raw, "trendRate")),
    trend: asString(field(raw, "trend")),
    heldFacts: asHeldFacts(field(raw, "heldFacts")),
    metBy: asMetBy(field(raw, "metBy")),
  };
}

/** Undefined when the body is not a dispatch the cards can render. */
export function parseAlertDispatchEvent(body: unknown): AlertDispatchEvent | undefined {
  if (typeof body !== "object" || body === null) return undefined;
  const deliveryId = field(body, "deliveryId");
  const channelType = field(body, "channelType");
  const destination = field(body, "destination");
  const tenantSlug = field(body, "tenantSlug");
  const payload = parseAlertPayload(field(body, "payload"));
  if (
    typeof deliveryId !== "string" ||
    typeof channelType !== "string" ||
    typeof destination !== "string" ||
    typeof tenantSlug !== "string" ||
    payload === undefined
  ) {
    return undefined;
  }
  return { deliveryId, channelType, destination, tenantSlug, payload };
}
