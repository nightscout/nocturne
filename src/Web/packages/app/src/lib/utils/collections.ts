type Key<T> = (item: T) => PropertyKey | null | undefined;

/** Distinct values in first-seen order, without null or undefined. */
export function distinct<T>(values: Iterable<T | null | undefined>): T[] {
  const out = new Set<T>();
  for (const value of values) {
    if (value != null) out.add(value);
  }
  return [...out];
}

/** The first item for each key, in order; items without a key are dropped. */
export function uniqueBy<T>(items: Iterable<T>, key: Key<T>): T[] {
  const seen = new Set<PropertyKey>();
  const out: T[] = [];
  for (const item of items) {
    const k = key(item);
    if (k == null || seen.has(k)) continue;
    seen.add(k);
    out.push(item);
  }
  return out;
}

/** Items bucketed by key, in first-seen order; items without a key are dropped. */
export function groupBy<T, K extends PropertyKey>(
  items: Iterable<T>,
  key: (item: T) => K | null | undefined
): Map<K, T[]> {
  const groups = new Map<K, T[]>();
  for (const item of items) {
    const k = key(item);
    if (k == null) continue;
    const group = groups.get(k);
    if (group) group.push(item);
    else groups.set(k, [item]);
  }
  return groups;
}

/**
 * Each key mapped to the value of the last item carrying it; items without a
 * key are dropped.
 */
export function indexBy<T, K extends PropertyKey, V>(
  items: Iterable<T>,
  key: (item: T) => K | null | undefined,
  value: (item: T) => V
): Map<K, V> {
  const index = new Map<K, V>();
  for (const item of items) {
    const k = key(item);
    if (k != null) index.set(k, value(item));
  }
  return index;
}

/**
 * A copy of `set` with `value` added or removed: flipped when `present` is
 * omitted. A `$state` Set is updated by assigning the copy.
 */
export function toggled<T>(set: ReadonlySet<T>, value: T, present = !set.has(value)): Set<T> {
  const next = new Set(set);
  if (present) next.add(value);
  else next.delete(value);
  return next;
}

/** A copy of `set` with every value added. */
export function withAll<T>(set: ReadonlySet<T>, values: Iterable<T>): Set<T> {
  const next = new Set(set);
  for (const value of values) next.add(value);
  return next;
}
