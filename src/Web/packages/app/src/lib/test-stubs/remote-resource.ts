/**
 * The remote-function shapes `$app/server` hands back, for the browser test
 * environment. `./app-server` builds its exports from these, and a test
 * standing in for a generated remote module builds its stand-ins from the same
 * ones, so there is one description of the shape rather than one per test.
 *
 * Mirrored: the surface a component touches. A query is a thenable carrying
 * `current`, `error`, `loading` and `ready` alongside `run`, `set`, `refresh`
 * and `withOverride`, and reading any of those getters starts the work, as the
 * framework's does. A command call is a promise carrying `updates()`, behind a
 * wrapper counting `pending`. `fields.issues()` and `fields.allIssues()` answer
 * `undefined`, not `[]`, when there is nothing to report: the framework builds
 * its issue map with `flatten_issues`, which yields `{}` for no issues, so the
 * lookup misses and the `?.map` short-circuits. Callers have to be written
 * against that.
 *
 * Not mirrored: reactivity, per-argument caching, schema validation, and form
 * submission. The getters read a snapshot rather than a signal, so nothing
 * re-renders when a pending resource settles; a test that needs a value on
 * screen builds the resource with `remoteQuery`, which is settled before the
 * first read and re-reads its source on every access, keeping whatever
 * reactivity that source has.
 *
 * Deliberately more permissive than production: the framework refuses `run()`
 * inside an effect and refuses to be awaited outside a tracking context, and
 * these resources allow both. Code that only a test exercises can rely on
 * either and still fail in the browser.
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

export type RemoteCommandCall<T> = Promise<T> & {
  updates: () => RemoteCommandCall<T>;
};

// `pending` is a getter on the framework's wrapper and a plain property here.
// Nothing writes it from outside, so the count a component reads is the same.
export interface RemoteCommandStub<T> {
  (arg?: unknown): RemoteCommandCall<T>;
  pending: number;
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
    // reads off `error` is not also an unhandled rejection. In the browser
    // runner an unhandled rejection aborts the whole run, so a resource nobody
    // awaits would take the suite down with it.
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

export function remoteCommand<T>(
  fn: (arg?: unknown) => T | Promise<T>
): RemoteCommandStub<T> {
  function invoke(arg?: unknown): RemoteCommandCall<T> {
    stub.pending++;

    const call: RemoteCommandCall<T> = Object.assign(
      (async () => {
        try {
          return await fn(arg);
        } finally {
          stub.pending--;
        }
      })(),
      { updates: () => call }
    );

    return call;
  }

  const stub: RemoteCommandStub<T> = Object.assign(invoke, { pending: 0 });

  return stub;
}

/**
 * Every field reports no issues, because the stub runs no schema and has
 * nothing to report. An unrecognised property nests, so a path of any depth
 * answers.
 */
function fieldProxy(path: string[] = []): unknown {
  const name = path.join(".");

  return new Proxy(() => undefined, {
    get(target, prop) {
      if (typeof prop === "symbol") return Reflect.get(target, prop);
      if (prop === "issues" || prop === "allIssues") return () => undefined;
      if (prop === "value") return () => undefined;
      if (prop === "set") return (value: unknown) => value;
      if (prop === "as") return (type: string) => ({ name, type });
      return fieldProxy([...path, prop]);
    },
  });
}

/**
 * The instance the framework's server build returns, where `pending` is always
 * 0 and `result` always undefined. Nothing here submits, so the client build's
 * counting `pending` would have nothing to count.
 */
export function remoteForm() {
  const action = "?/remote=stub";
  const instance: Record<string, unknown> = { method: "POST", action };

  // `method` and `action` are the framework's only enumerable own properties
  // on the server instance, and the two the caller spreads onto a <form>. The
  // client instance additionally carries an attachment under a symbol key,
  // which drives submission the stub does not implement.
  Object.defineProperties(instance, {
    enhance: { value: () => ({ method: "POST", action }) },
    fields: { get: () => fieldProxy() },
    result: { get: () => undefined },
    pending: { get: () => 0 },
    preflight: { value: () => instance },
    validate: { value: async () => {} },
    for: { value: () => instance },
  });

  return instance;
}
