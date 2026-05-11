/**
 * Deep equality comparison for plain objects, arrays, dates, and primitives.
 * Designed for comparing Zod-schema-shaped form values — not a general-purpose utility.
 * Treats undefined values and missing keys as equivalent.
 */
export function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a == null || b == null) return false;

  if (a instanceof Date && b instanceof Date) return a.getTime() === b.getTime();

  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) return false;
    return a.every((item, i) => deepEqual(item, b[i]));
  }

  if (typeof a === "object" && typeof b === "object" && !Array.isArray(a) && !Array.isArray(b)) {
    const aObj = a as Record<string, unknown>;
    const bObj = b as Record<string, unknown>;
    const keys = new Set([...Object.keys(aObj), ...Object.keys(bObj)]);
    for (const key of keys) {
      const aVal = aObj[key];
      const bVal = bObj[key];
      if (aVal === undefined && bVal === undefined) continue;
      if (!deepEqual(aVal, bVal)) return false;
    }
    return true;
  }

  return false;
}
