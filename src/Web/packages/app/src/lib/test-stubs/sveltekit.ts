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

// Returns never in @sveltejs/kit: callers invoke it bare and rely on it aborting,
// so returning here would let a rejected validation fall through to the success path.
export function invalid(...issues: any[]): never {
  throw new Error(`Invalid: ${JSON.stringify(issues)}`);
}
