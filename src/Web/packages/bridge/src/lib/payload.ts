/** A payload relayed from the API. The bridge forwards its fields without owning their shape. */
export type Payload = Record<string, unknown>;

export const isRecord = (value: unknown): value is Payload =>
  typeof value === 'object' && value !== null;

/** `value[key]` when `value` is an object holding a string there. */
export function stringField(value: unknown, key: string): string | undefined {
  if (!isRecord(value)) return undefined;
  const field = value[key];
  return typeof field === 'string' ? field : undefined;
}

/**
 * A payload's own fields, read the way property access and spread read them: a nullish
 * payload throws, and a primitive has none of the fields the translators read (a string
 * still spreads its characters).
 */
export function fieldsOf(value: unknown): Payload {
  if (value === null || value === undefined) {
    throw new TypeError(`Cannot read fields of ${value}`);
  }
  return typeof value === 'object' ? { ...value } : Object.assign({}, value);
}
