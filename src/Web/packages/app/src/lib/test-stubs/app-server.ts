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
 * A remote module a test forgot to `vi.mock` no longer fails loudly: the
 * implementation runs, reaches `getRequestEvent`, and that error settles on the
 * resource's `error` instead of propagating, so a component reading only
 * `current` renders empty. A query that never produces a value is the shape a
 * missing `vi.mock` takes.
 *
 * See `./remote-resource` for what these shapes do and do not carry.
 */
import {
  createQueryResource,
  remoteCommand,
  remoteForm,
} from "./remote-resource";

export function getRequestEvent(): never {
  throw new Error("getRequestEvent is not available in browser tests");
}

type Implementation = (arg?: unknown) => unknown;

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
  return remoteCommand(implementation(validateOrFn, maybeFn));
}

export const form = remoteForm;
