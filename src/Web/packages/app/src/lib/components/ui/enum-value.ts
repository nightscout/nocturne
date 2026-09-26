/**
 * The member of a string enum equal to `value`, or `undefined`. A Select or
 * toggle reports its value as a bare string; this is how it becomes the enum
 * the model holds.
 */
export function enumValue<E extends Record<string, string>>(
  enumObject: E,
  value: unknown
): E[keyof E] | undefined {
  const members: E[keyof E][] = Object.values(enumObject);
  return members.find((member) => member === value);
}

/**
 * The label a lookup table holds for `key`, or `undefined` for a key it does not
 * name. Own keys only, so "toString" and friends do not resolve to functions.
 */
export function labelFor(
  labels: Readonly<Record<string, string>>,
  key: string | null | undefined
): string | undefined {
  if (key == null) return undefined;
  return Object.entries(labels).find(([name]) => name === key)?.[1];
}
