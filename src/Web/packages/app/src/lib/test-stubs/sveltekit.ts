/**
 * Stub for @sveltejs/kit in browser test environment.
 * Remote functions import error/redirect but never call them in component tests.
 */
export function error(status: number, body?: any) {
  throw new Error(`HTTP ${status}: ${body}`);
}

export function redirect(status: number, location: string) {
  throw new Error(`Redirect ${status}: ${location}`);
}

export function json(data: any, init?: ResponseInit) {
  return new Response(JSON.stringify(data), init);
}

export function fail(status: number, data?: any) {
  return { status, data };
}
