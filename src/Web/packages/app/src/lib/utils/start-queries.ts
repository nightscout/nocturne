/**
 * Sends each remote query's request now, together.
 *
 * A remote query created in a component is fetched on its first read of `current`, `loading`,
 * `error`, `ready` or `then`, not when it is created. A page whose reads come one after another
 * (`a.loading || b.loading`, consecutive `await`s in markup, a read inside an `{#if}` that waits
 * on another query) therefore sends its queries one round-trip apart. Calling this during
 * component initialization, with every query the page needs on arrival, sends them at once.
 *
 * Never pass a query held in a `$derived`. Read here, the derived runs unconnected; the template's
 * first read then connects it and re-runs the query's own effect, whose teardown releases the
 * query's cache reference without the re-run taking it back, so `current` never fills
 * (sveltejs/kit#14915).
 */
export function startQueries(...queries: ({ readonly loading: boolean } | null | undefined)[]): void {
  for (const query of queries) void query?.loading;
}
