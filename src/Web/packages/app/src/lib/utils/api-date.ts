/**
 * The generated API client types date fields as `Date`, but it parses responses with a
 * plain `JSON.parse` and no reviver — the values arrive as ISO strings. Calling a `Date`
 * method straight off a client field throws, so normalise through here first.
 */
export function toIsoString(value: Date | string | null | undefined): string | null {
  if (value == null || value === "") return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}
