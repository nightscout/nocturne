/**
 * Builds a value from a key, returning the previous instance whenever the key
 * is unchanged.
 *
 * Svelte re-evaluates a `$derived` whenever a dependency _may_ have changed,
 * and a derived it has flagged dirty is re-executed on every read for as long
 * as more than one batch is alive — which is the whole time a page-level
 * `<svelte:boundary>` is awaiting. A derived that allocates therefore publishes
 * a new identity on each of those reads and reports a change it did not have.
 * Where the result feeds a chart, that rebuilds the whole series and every
 * scale below it, and the effects that reconciles re-enter the flush that
 * provoked them. Keying the allocation makes the comparison succeed, so a
 * re-evaluation costs nothing and propagates nothing.
 *
 * **Key parts are compared by identity** (`Object.is`), which is what makes the
 * comparison cheap enough to run on every read. A key part that is an object is
 * therefore only safe if it is replaced wholesale on every change —
 * `$state.raw` holding an array that is always reassigned, never pushed into.
 * One mutated in place looks unchanged and this returns a stale value
 * indefinitely.
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

    // Committed only once `build` has returned: recording the new key beside the
    // old value would serve that stale value to every later call with this key.
    previous = build(...key);
    previousKey = key;
    return previous;
  };
}
