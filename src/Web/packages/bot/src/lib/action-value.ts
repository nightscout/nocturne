/**
 * Encoding for the `value` a card button round-trips back to its handler.
 *
 * A card addresses one tenant and the excursion its buttons act on. Both are
 * UUIDs, and both together do not fit: Telegram caps `callback_data` at 64
 * bytes, and `encodeTelegramCallbackData` in `@chat-adapter/telegram` spends
 * `chat:{"a":"<actionId>","v":"<value>"}` on the envelope — 20 bytes plus the
 * action id, leaving 35 for the value under `ack_alert`, the longest id an alert
 * card carries. Two 36-character UUIDs need 73, and 32 raw bytes cannot be
 * encoded into 35 printable ones either, so a tighter encoding alone is not
 * enough. Over the limit the adapter throws and the whole delivery is marked
 * failed, so the budget binds every adapter, not just Telegram.
 *
 * The excursion therefore travels as the base64url of its 16 raw bytes (22
 * chars, lossless — the handler acknowledges it by id), and the tenant as a
 * prefix of the same encoding, long enough only to pick one of the tapping
 * user's own linked tenants out of a handful of server-generated UUIDs.
 * 8 + 1 + 22 = 31 bytes, 60 with the `ack_alert` envelope.
 */
const SEPARATOR = ":";
const TENANT_KEY_CHARS = 8;

const UUID = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const PACKED_UUID = /^[A-Za-z0-9_-]{22}$/;

const packUuid = (uuid: string) =>
  Buffer.from(uuid.replace(/-/g, ""), "hex").toString("base64url");

const unpackUuid = (packed: string) => {
  const hex = Buffer.from(packed, "base64url").toString("hex");
  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    hex.slice(12, 16),
    hex.slice(16, 20),
    hex.slice(20),
  ].join("-");
};

/** Reads either encoding, so buttons on cards posted before the budget fix still work. */
const readUuid = (segment: string | undefined): string | null => {
  if (!segment) return null;
  if (UUID.test(segment)) return segment;
  return PACKED_UUID.test(segment) ? unpackUuid(segment) : null;
};

export interface ActionTarget {
  /**
   * Names the tenant the card was posted for, or null if the value names none.
   * Opaque: match it against a candidate with {@link encodeTenantKey} rather
   * than treating it as an id.
   */
  tenantKey: string | null;
  /** Excursion the button acts on, or null if the value addresses only a tenant. */
  excursionId: string | null;
}

/** The key a tenant id is named by inside a card button value. */
export function encodeTenantKey(tenantId: string): string {
  return packUuid(tenantId).slice(0, TENANT_KEY_CHARS);
}

export function encodeActionValue(target: {
  tenantId: string;
  excursionId: string;
}): string {
  return `${encodeTenantKey(target.tenantId)}${SEPARATOR}${packUuid(target.excursionId)}`;
}

/**
 * A value with no second segment addresses only a tenant. An excursion is
 * meaningless without the tenant to resolve it against, so a value whose first
 * segment is empty yields neither.
 */
export function decodeActionValue(value: string | null | undefined): ActionTarget {
  const [tenant, excursion] = (value ?? "").split(SEPARATOR);
  if (!tenant) return { tenantKey: null, excursionId: null };
  return {
    tenantKey: UUID.test(tenant) ? encodeTenantKey(tenant) : tenant,
    excursionId: readUuid(excursion),
  };
}
