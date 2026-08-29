/**
 * Builds a value from primitives, returning the previous instance whenever
 * those primitives are unchanged.
 *
 * Svelte re-evaluates a `$derived` whenever a dependency _may_ have changed,
 * and a derived it has flagged dirty is re-executed on every read for as long
 * as more than one batch is alive — which is the whole time a page-level
 * `<svelte:boundary>` is awaiting. A derived that allocates therefore publishes
 * a new identity on each of those reads and reports a change it did not have.
 * Where the result feeds a chart, that rebuilds the whole series and every
 * scale below it, and the effects that reconciles re-enter the flush that
 * provoked them.
 *
 * Keying the allocation on primitives makes the comparison succeed, so a
 * re-evaluation costs nothing and propagates nothing.
 *
 * Each call returns a memo with its own one-entry cache, so build one per thing
 * being kept stable — never one shared across component or store instances.
 */
export function stableBy<K extends readonly unknown[], T>(
  build: (...key: K) => T
): (...key: K) => T {
  let previousKey: readonly unknown[] | null = null;
  let previous: T;

  return (...key: K): T => {
    if (previousKey !== null && previousKey.length === key.length) {
      const seen = previousKey.values();
      let unchanged = true;
      for (const value of key) {
        if (!Object.is(seen.next().value, value)) {
          unchanged = false;
          break;
        }
      }
      if (unchanged) return previous;
    }

    previousKey = key;
    previous = build(...key);
    return previous;
  };
}
