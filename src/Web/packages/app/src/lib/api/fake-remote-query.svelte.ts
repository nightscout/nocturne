/**
 * Stand-in for a generated remote `query()` export, for component tests. Every call shares one
 * result, as a real query memoized on its payload does, so a component that reads the query in
 * one place and refreshes it in another sees the one value.
 */

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
    if (state.settled === null) state.run();

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
        const pending = state.settled ?? state.run();
        return pending.then.bind(pending);
      },
    };
  };
}
