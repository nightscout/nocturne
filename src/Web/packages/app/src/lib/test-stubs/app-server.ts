/**
 * Stub for $app/server in browser test environment.
 *
 * `query`, `command` and `form` return what the framework returns — a wrapper
 * whose call yields a query resource, a promise carrying `updates()`, and a
 * form instance respectively — not the implementation handed in. A stub that
 * returned the implementation would let a component test pass against a
 * component reading `.current` or calling `.updates()`, neither of which exists
 * on a bare function in production, and would drop the second argument of the
 * `(schema, fn)` overloads entirely.
 *
 * See `./remote-resource` for what a query resource does and does not carry.
 * Form submission is not plumbed: `enhance()` returns the attributes and no
 * listener, so the implementation only ever runs for a query or a command.
 */
import { createQueryResource } from "./remote-resource";

export function getRequestEvent(): never {
  throw new Error("getRequestEvent is not available in browser tests");
}

type Implementation = (arg?: unknown) => unknown;
type CommandCall = Promise<unknown> & { updates: () => CommandCall };

// The framework's `(schema, fn)` and `(fn)` overloads both end at `fn`. Only
// the vitest alias reaches this module, so the schema never needs a type here.
function implementation(
  validateOrFn: Implementation,
  maybeFn?: Implementation
): Implementation {
  return maybeFn ?? validateOrFn;
}

export function query(validateOrFn: Implementation, maybeFn?: Implementation) {
  const fn = implementation(validateOrFn, maybeFn);

  return (arg?: unknown) => createQueryResource(async () => fn(arg));
}

export function command(
  validateOrFn: Implementation,
  maybeFn?: Implementation
) {
  const fn = implementation(validateOrFn, maybeFn);
  let pending = 0;

  const wrapper = (arg?: unknown): CommandCall => {
    pending++;

    const call: CommandCall = Object.assign(
      (async () => {
        try {
          return await fn(arg);
        } finally {
          pending--;
        }
      })(),
      { updates: () => call }
    );

    return call;
  };

  Object.defineProperty(wrapper, "pending", { get: () => pending });

  return wrapper;
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
      if (prop === "issues" || prop === "allIssues") return () => [];
      if (prop === "value") return () => undefined;
      if (prop === "set") return (value: unknown) => value;
      if (prop === "as") return (type: string) => ({ name, type });
      return fieldProxy([...path, prop]);
    },
  });
}

export function form() {
  const action = "?/remote=stub";
  const instance: Record<string, unknown> = { method: "POST", action };

  // Only `method` and `action` are enumerable on the framework's instance, so
  // only they land on the element when the form is spread onto one.
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
