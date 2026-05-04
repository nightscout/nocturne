/**
 * Stub for $app/server in browser test environment.
 * Remote functions import this but are never called in component tests.
 */
export function getRequestEvent() {
  throw new Error("getRequestEvent is not available in browser tests");
}

export function query(fn: any) {
  return fn;
}

export function command(fn: any) {
  return fn;
}

export function form(fn: any) {
  return fn;
}
