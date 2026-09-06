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
 * chars, lossless — the handler acknowledges it by id), and the tenant as the
 * *last* 8 characters of the same encoding. The slice has to come off the tail:
 * a tenant id is a UUIDv7 whose leading 48 bits are its creation millisecond,
 * so tenants provisioned concurrently share their head outright, while the
 * trailing bytes are rand_b and are random. Those 8 characters name one of the
 * tapping user's own linked tenants; a key matching two of them is refused by
 * {@link pickCandidate} rather than guessed. 8 + 1 + 22 = 31 bytes, 60 with the
 * `ack_alert` envelope.
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

/** Reads an excursion segment written either as a full UUID or as its packed form. */
const readUuid = (segment: string): string | null => {
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
  /** Excursion the button acts on, or null if the value names none or names one that cannot be read. */
  excursionId: string | null;
  /**
   * The value names an excursion that does not decode. The action has to fail:
   * widening to every alert of the tenant would silence more than the card is
   * about.
   */
  unreadableExcursion: boolean;
}

/** The key a tenant id is named by inside a card button value. */
export function encodeTenantKey(tenantId: string): string {
  return packUuid(tenantId).slice(-TENANT_KEY_CHARS);
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
  if (!tenant) return { tenantKey: null, excursionId: null, unreadableExcursion: false };
  const excursionId = excursion ? readUuid(excursion) : null;
  return {
    tenantKey: UUID.test(tenant) ? encodeTenantKey(tenant) : tenant,
    excursionId,
    unreadableExcursion: Boolean(excursion) && excursionId === null,
  };
}
