/**
 * The remote-function shapes `$app/server` hands back, for the browser test
 * environment.
 *
 * Mirrored: the surface a component touches. A query is a thenable carrying
 * `current`, `error`, `loading` and `ready` alongside `run`, `set`, `refresh`
 * and `withOverride`, and reading any of those getters starts the work, as the
 * framework's does.
 *
 * Not mirrored: reactivity, per-argument caching, and schema validation. The
 * getters read a snapshot rather than a signal, so nothing re-renders when a
 * pending resource settles. A test that needs a value on screen builds the
 * resource with `remoteQuery`, which is settled before the first read.
 */
export interface RemoteQueryStub<T> extends PromiseLike<T> {
  readonly current: T | undefined;
  readonly error: unknown;
  readonly loading: boolean;
  readonly ready: boolean;
  run(): Promise<T>;
  set(value: T): void;
  refresh(): Promise<void>;
  withOverride(update: (current: T) => T): () => void;
}

class QueryResource<T> implements RemoteQueryStub<T> {
  #run: () => Promise<T>;
  #read: (() => T) | null = null;
  #promise: Promise<void> | null = null;
  #loading = false;
  #error: unknown = undefined;
  #overrides: ((current: T) => T)[] = [];

  constructor(run: () => Promise<T>) {
    this.#run = run;
  }

  #start(): Promise<void> {
    if (this.#promise) return this.#promise;

    this.#loading = true;

    const promise = (async () => this.#run())().then(
      (value) => {
        this.#read = () => value;
        this.#loading = false;
        this.#error = undefined;
      },
      (reason) => {
        this.#loading = false;
        this.#error = reason;
        throw reason;
      }
    );

    // The framework keeps a handler on its own copy so a rejection a component
    // reads off `error` is not also an unhandled rejection.
    promise.catch(() => {});

    this.#promise = promise;
    return promise;
  }

  settle(read: () => T) {
    this.#read = read;
    this.#loading = false;
    this.#error = undefined;
    this.#promise = Promise.resolve();
  }

  #overridden(read: () => T): T {
    return this.#overrides.reduce<T>((value, apply) => apply(value), read());
  }

  get current(): T | undefined {
    void this.#start();
    return this.#read ? this.#overridden(this.#read) : undefined;
  }

  get error(): unknown {
    void this.#start();
    return this.#error;
  }

  get loading(): boolean {
    void this.#start();
    return this.#loading;
  }

  get ready(): boolean {
    void this.#start();
    return this.#read !== null;
  }

  then<TResult1 = T, TResult2 = never>(
    onfulfilled?:
      | ((value: T) => TResult1 | PromiseLike<TResult1>)
      | null
      | undefined,
    onrejected?:
      | ((reason: unknown) => TResult2 | PromiseLike<TResult2>)
      | null
      | undefined
  ): Promise<TResult1 | TResult2> {
    return this.run().then(onfulfilled, onrejected);
  }

  catch<TResult = never>(
    onrejected?:
      | ((reason: unknown) => TResult | PromiseLike<TResult>)
      | null
      | undefined
  ): Promise<T | TResult> {
    return this.run().catch(onrejected);
  }

  finally(onfinally?: (() => void) | null | undefined): Promise<T> {
    return this.run().finally(onfinally);
  }

  run(): Promise<T> {
    return this.#start().then(() => {
      if (!this.#read) throw new Error("the resource settled without a value");
      return this.#overridden(this.#read);
    });
  }

  set(value: T) {
    this.settle(() => value);
  }

  refresh(): Promise<void> {
    this.#promise = null;
    return this.#start();
  }

  withOverride(update: (current: T) => T): () => void {
    this.#overrides.push(update);

    return () => {
      const index = this.#overrides.indexOf(update);
      if (index !== -1) this.#overrides.splice(index, 1);
    };
  }
}

export function createQueryResource<T>(
  run: () => Promise<T>
): RemoteQueryStub<T> {
  return new QueryResource(run);
}

/**
 * A query resource that is already settled, for tests standing in for a remote
 * module. `read` is called on every access, so a test can move the value the
 * component sees between renders.
 */
export function remoteQuery<T>(read: () => T): RemoteQueryStub<T> {
  const resource = new QueryResource<T>(async () => read());
  resource.settle(read);
  return resource;
}
