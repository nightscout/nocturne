/**
 * Encoding for the `value` a card button round-trips back to its handler.
 *
 * A card addresses one tenant and, when the button acts on a single alert, one
 * excursion. Both are UUIDs, so a colon separates them unambiguously.
 */
const SEPARATOR = ":";

export interface ActionTarget {
  /** Tenant the card was posted for, or null if the value names none. */
  tenantId: string | null;
  /** Excursion the button acts on, or null if the value addresses only a tenant. */
  excursionId: string | null;
}

export function encodeActionValue(target: {
  tenantId: string;
  excursionId: string;
}): string {
  return `${target.tenantId}${SEPARATOR}${target.excursionId}`;
}

/**
 * A value with no second segment addresses only a tenant. An excursion is
 * meaningless without the tenant to resolve it against, so a value whose first
 * segment is empty yields neither.
 */
export function decodeActionValue(value: string | null | undefined): ActionTarget {
  const [tenantId, excursionId] = (value ?? "").split(SEPARATOR);
  if (!tenantId) return { tenantId: null, excursionId: null };
  return { tenantId, excursionId: excursionId || null };
}
