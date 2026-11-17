/**
 * The remote-function shapes `$app/server` hands back, for the browser test
 * environment. `./app-server` builds its exports from these, and a test
 * standing in for a generated remote module builds its stand-ins from the same
 * ones, so there is one description of the shape rather than one per test.
 *
 * Mirrored: the surface a component touches. A query is a thenable carrying
 * `current`, `error`, `loading` and `ready` alongside `set`, `refresh` and
 * `withOverride`, and reading any of those getters starts the work, as the
 * framework's does. A command call is a promise carrying `updates()`, behind a
 * wrapper counting `pending` and recording the queries each `updates()` named;
 * an override handed to `updates()` is released once the call settles, so a
 * failed command rolls it back. `fields.issues()` and `fields.allIssues()` answer
 * `undefined`, not `[]`, when there is nothing to report: the framework builds
 * its issue map with `flatten_issues`, which yields `{}` for no issues, so the
 * lookup misses and the `?.map` short-circuits. Callers have to be written
 * against that. A form built with `form()`'s arguments submits; see `remoteForm`.
 *
 * Not mirrored: reactivity beyond overrides and form state, and per-argument
 * caching. Adding or releasing an override re-renders
 * whatever read `current`, but the getters otherwise read a snapshot rather than
 * a signal, so nothing re-renders when a pending resource settles; a test that needs a value on
 * screen builds the resource with `remoteQuery`, which is settled before the
 * first read and re-reads its source on every access, keeping whatever
 * reactivity that source has.
 */
import { createSubscriber } from "svelte/reactivity";
import type { StandardSchema } from "#lib/api/kit-form-key.js";

export interface RemoteQueryStub<T> extends PromiseLike<T> {
  readonly current: T | undefined;
  readonly error: unknown;
  readonly loading: boolean;
  readonly ready: boolean;
  set(value: T): void;
  refresh(): Promise<void>;
  withOverride(update: (current: T) => T): () => void;
}

export type RemoteCommandCall<T> = Promise<T> & {
  updates: (...queries: unknown[]) => RemoteCommandCall<T>;
};

// `pending` is a getter on the framework's wrapper and a plain property here.
// Nothing writes it from outside, so the count a component reads is the same.
export interface RemoteCommandStub<T> {
  (arg?: unknown): RemoteCommandCall<T>;
  pending: number;
  updated: unknown[][];
}

/** The queries each `updates()` call on a mocked command named, in call order. */
export function updatedBy(command: unknown): unknown[][] {
  return (command as RemoteCommandStub<unknown>).updated;
}

const overrideReleases = new WeakSet<() => void>();

class QueryResource<T> implements RemoteQueryStub<T> {
  #run: () => Promise<T>;
  #source: (() => T) | null;
  #read: (() => T) | null = null;
  #promise: Promise<void> | null = null;
  #loading = false;
  #error: unknown = undefined;
  #overrides: ((current: T) => T)[] = [];
  #overridesChanged: (() => void) | null = null;
  #subscribe = createSubscriber((update) => {
    this.#overridesChanged = update;
    return () => {
      this.#overridesChanged = null;
    };
  });

  constructor(run: () => Promise<T>, source: (() => T) | null = null) {
    this.#run = run;
    this.#source = source;
  }

  #start(): Promise<void> {
    if (this.#promise) return this.#promise;

    this.#loading = true;

    const promise = (async () => this.#run())().then(
      (value) => {
        this.#read = this.#source ?? (() => value);
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

  settleError(error: unknown) {
    this.#read = null;
    this.#loading = false;
    this.#error = error;
    this.#promise = Promise.resolve();
  }

  #overridden(read: () => T): T {
    return this.#overrides.reduce<T>((value, apply) => apply(value), read());
  }

  get current(): T | undefined {
    this.#subscribe();
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
    return this.#value().then(onfulfilled, onrejected);
  }

  catch<TResult = never>(
    onrejected?:
      | ((reason: unknown) => TResult | PromiseLike<TResult>)
      | null
      | undefined
  ): Promise<T | TResult> {
    return this.#value().catch(onrejected);
  }

  finally(onfinally?: (() => void) | null | undefined): Promise<T> {
    return this.#value().finally(onfinally);
  }

  #value(): Promise<T> {
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
    this.#overridesChanged?.();

    const release = () => {
      const index = this.#overrides.indexOf(update);
      if (index === -1) return;
      this.#overrides.splice(index, 1);
      this.#overridesChanged?.();
    };
    overrideReleases.add(release);
    return release;
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
  const resource = new QueryResource<T>(async () => read(), read);
  resource.settle(read);
  return resource;
}

/**
 * A query resource that is already settled into an error, for a test standing
 * in for a remote module whose call rejected (e.g. a 403). `current` stays
 * undefined and `error` holds the reason, synchronously, so a component reads
 * the failed state on its first render rather than hanging on a pending fetch.
 */
export function remoteQueryError<T>(error: unknown): RemoteQueryStub<T> {
  const resource = new QueryResource<T>(async () => {
    throw error;
  });
  resource.settleError(error);
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
      {
        updates: (...queries: unknown[]) => {
          stub.updated.push(queries);
          for (const query of queries) {
            if (overrideReleases.has(query as () => void)) {
              void call.then(query as () => void, query as () => void);
            }
          }
          return call;
        },
      }
    );

    return call;
  }

  const stub: RemoteCommandStub<T> = Object.assign(invoke, {
    pending: 0,
    updated: [],
  });

  return stub;
}

/** The props kit's `as()` gives a control, without the form id it suffixes onto the name. */
function fieldProps(name: string, type: string, value: unknown): Record<string, unknown> {
  const prefix =
    type === "number" || type === "range"
      ? "n:"
      : type === "checkbox" && typeof value !== "string"
        ? "b:"
        : (type === "hidden" || type === "submit") && typeof value === "number"
          ? "n:"
          : (type === "hidden" || type === "submit") && typeof value === "boolean"
            ? "b:"
            : "";
  const props: Record<string, unknown> = { name: prefix + name, type };
  if (type === "hidden" || type === "submit") {
    props.value = typeof value === "boolean" ? (value ? "on" : "off") : value;
  }
  return props;
}

interface FormIssue {
  path: (string | number)[];
  message: string;
}

type FormHandler = (data: never) => unknown;

/**
 * A remote form's `fields`, for a test that builds its own form stand-in: any path, each with
 * `issues`, `allIssues`, `value`, `set` and `as`, the last answering the name kit would give it.
 * An unrecognised property nests, so a path of any depth answers.
 */
export function remoteFormFields(): unknown {
  return fieldProxy();
}

function fieldProxy(
  path: string[] = [],
  issuesOf: () => FormIssue[] = () => []
): unknown {
  // As kit builds it: an index is `[n]`, a key is `.key`.
  const name = path
    .map((key, i) => (/^\d+$/.test(key) ? `[${key}]` : i === 0 ? key : `.${key}`))
    .join("");

  const within = (issue: FormIssue, exact: boolean) =>
    (!exact || issue.path.length === path.length) &&
    path.every((key, i) => String(issue.path[i]) === key);

  const answer = (exact: boolean) => () => {
    const found = issuesOf().filter((issue) => within(issue, exact));
    return found.length > 0 ? found : undefined;
  };

  return new Proxy(() => undefined, {
    get(target, prop) {
      if (typeof prop === "symbol") return Reflect.get(target, prop);
      if (prop === "issues") return answer(true);
      if (prop === "allIssues") return answer(false);
      if (prop === "value") return () => undefined;
      if (prop === "set") return (value: unknown) => value;
      if (prop === "as") return (type: string, value?: unknown) => fieldProps(name, type, value);
      return fieldProxy([...path, prop], issuesOf);
    },
  });
}

/**
 * The input kit's `convert_formdata` builds from a submitted form: an `n:` or `b:` name prefix
 * types the value, and dots and `[n]` nest it.
 */
function formInput(data: FormData): Record<string, unknown> {
  const input: Record<string, unknown> = {};

  for (const name of new Set(data.keys())) {
    const values = data.getAll(name).filter((v): v is string => typeof v === "string");
    const [, type, field] = /^(?:([nb]):)?(.*)$/.exec(name)!;
    const isArray = field.endsWith("[]");
    const keys = (isArray ? field.slice(0, -2) : field).split(/\.|\[|\]/).filter(Boolean);
    const coerce = (value: string) =>
      type === "n" ? (value === "" ? undefined : parseFloat(value)) : type === "b" ? value === "on" : value;

    let target = input;
    keys.slice(0, -1).forEach((key, i) => {
      target = (target[key] ??= /^\d+$/.test(keys[i + 1]) ? [] : {}) as Record<string, unknown>;
    });
    target[keys.at(-1)!] = isArray ? values.map(coerce) : coerce(values[0]);
  }

  return input;
}

/**
 * A remote form, as the framework's client build hands it back. Without a handler it is the
 * server build's instance: nothing a test does submits it, `pending` stays 0 and `result`
 * undefined.
 *
 * Given `form()`'s arguments, an enhanced form submits the way kit's does. The browser builds the
 * input from the form's controls, a `.for(key)` instance adds `id: key` when the form posts no
 * `id`, and the schema validates the result. Its issues reach `fields.issues()` and
 * `fields.allIssues()`, and `submit()` resolves false. Otherwise the handler's return value
 * becomes `result` and `submit()` resolves true, and a handler that throws rejects `submit()`.
 * A rejection the enhance callback lets through is left unhandled, as kit hands it to its error
 * page.
 */
export function remoteForm(
  schemaOrHandler?: StandardSchema | "unchecked" | FormHandler,
  handler?: FormHandler
) {
  const schema = typeof schemaOrHandler === "object" ? schemaOrHandler : undefined;
  const run = typeof schemaOrHandler === "function" ? schemaOrHandler : handler;
  const keyed = new Map<string, Record<string, unknown>>();

  const create = (key?: string | number | boolean) => {
    const action = key === undefined ? "?/remote=stub" : `?/remote=stub/${JSON.stringify(key)}`;
    const instance: Record<string, unknown> = { method: "POST", action };

    let result: unknown = undefined;
    let issues: FormIssue[] = [];
    let pending = 0;
    let changed: (() => void) | null = null;
    const subscribe = createSubscriber((update) => {
      changed = update;
      return () => {
        changed = null;
      };
    });
    const read = <T>(value: () => T) => () => {
      subscribe();
      return value();
    };

    async function submit(data: Record<string, unknown>): Promise<boolean> {
      pending++;
      changed?.();
      try {
        const validated = await schema?.["~standard"].validate(data);
        issues = (validated?.issues ?? []).map((issue) => ({
          message: issue.message,
          path: (issue.path ?? []).map((segment) => {
            const key = typeof segment === "object" ? segment.key : segment;
            return typeof key === "number" ? key : String(key);
          }),
        }));
        if (issues.length > 0) {
          result = undefined;
          return false;
        }
        result = await run?.(data as never);
        return true;
      } catch (error) {
        result = undefined;
        issues = [];
        throw error;
      } finally {
        pending--;
        changed?.();
      }
    }

    type EnhanceCallback = (helpers: { submit: () => Promise<boolean>; element: HTMLFormElement }) => unknown;

    // `method` and `action` are the framework's only enumerable own properties
    // on the instance, and the two the caller spreads onto a <form>.
    Object.defineProperties(instance, {
      enhance: {
        value: (callback: EnhanceCallback) => ({
          method: "POST",
          action,
          onsubmit: (event: SubmitEvent) => {
            if (!run) return;
            event.preventDefault();
            const element = event.currentTarget as HTMLFormElement;
            const data = formInput(new FormData(element, event.submitter));
            if (key !== undefined && !("id" in data)) data.id = key;
            void callback({ submit: () => submit(data), element });
          },
        }),
      },
      fields: { get: () => fieldProxy([], read(() => issues)) },
      result: { get: read(() => result) },
      pending: { get: read(() => pending) },
      preflight: { value: () => instance },
      validate: { value: async () => {} },
      for: {
        value: (forKey: string | number | boolean) => {
          const cacheKey = JSON.stringify(forKey);
          let found = keyed.get(cacheKey);
          if (!found) keyed.set(cacheKey, (found = create(forKey)));
          return found;
        },
      },
    });

    return instance;
  };

  return create();
}
