/**
 * Stand-in for a generated remote `query()` export, for component tests.
 *
 * A real query proxy decides once, in its constructor, whether it was built in a
 * tracking context, and refuses to be awaited when it was not — the failure the
 * `nocturne/no-imperative-remote-query` lint rule exists to catch. Tests that
 * hand a component a bare `Promise` cannot see that, so a component which awaits
 * a query from an event handler passes its tests and fails in the browser.
 *
 * Tracking is probed here the same way SvelteKit probes it, so the double agrees
 * with the runtime on the one thing it exists to model. That probe only carries
 * information under the browser runner: Svelte's server build makes
 * `$effect.pre` a no-op rather than a throw, so a Node test always reads as
 * tracking and cannot exercise the rule at all.
 *
 * @see $lib/api/retain-query.svelte for the registration lifetime this implies.
 */

/** SvelteKit's own tracking probe: `$effect.pre` throws outside a reactive context. */
function isTracking(): boolean {
  try {
    $effect.pre(() => {});
    return true;
  } catch {
    return false;
  }
}

const NOT_REACTIVE =
  "This query was not created in a reactive context and cannot be awaited. Use `.run()` to execute the query instead.";

export interface FakeQuery<T> extends PromiseLike<T> {
  readonly current: T | undefined;
  readonly error: unknown;
  readonly ready: boolean;
  readonly loading: boolean;
  refresh(): Promise<T>;
}

class FakeQueryState<T> {
  raw = $state.raw<T | undefined>(undefined);
  err = $state.raw<unknown>(undefined);
  ready = $state(false);
  loading = $state(false);
  settled: Promise<T> | null = null;

  constructor(private readonly load: () => Promise<T>) {}

  run(): Promise<T> {
    this.loading = true;

    const pending = this.load().then(
      (value) => {
        this.raw = value;
        this.err = undefined;
        this.ready = true;
        this.loading = false;
        return value;
      },
      (e: unknown) => {
        this.err = e;
        this.loading = false;
        throw e;
      }
    );

    // A rejection reaches whoever awaits the query or its refresh; nothing else
    // is expected to handle it.
    pending.catch(() => {});
    this.settled = pending;
    return pending;
  }
}

/**
 * Builds a fake `query()` export over `load`. Every call shares one underlying
 * result, as a real query memoized on its payload does.
 */
export function fakeRemoteQuery<T>(load: () => Promise<T>): () => FakeQuery<T> {
  const state = new FakeQueryState(load);

  return () => {
    const tracking = isTracking();

    if (tracking && state.settled === null) state.run();

    return {
      get current() {
        return state.raw;
      },
      get error() {
        return state.err;
      },
      get ready() {
        return state.ready;
      },
      get loading() {
        return state.loading;
      },
      refresh: () => state.run(),
      get then() {
        if (!tracking) throw new Error(NOT_REACTIVE);
        const pending = state.settled ?? state.run();
        return pending.then.bind(pending);
      },
    };
  };
}
