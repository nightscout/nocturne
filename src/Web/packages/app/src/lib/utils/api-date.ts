/**
 * The ISO 8601 form the API's date-time fields and parameters take, or null when the
 * value names no valid instant (so a cleared input sends no bound rather than throwing).
 */
export function toIsoString(value: Date | string | null | undefined): string | null {
  if (value == null || value === "") return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}
