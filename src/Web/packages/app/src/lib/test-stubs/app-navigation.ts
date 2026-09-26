/**
 * Stub for $app/navigation in browser test environment.
 */
export function goto(_url: string, _opts?: unknown) {
  return Promise.resolve();
}

export function invalidate(_url: string) {
  return Promise.resolve();
}

export function invalidateAll() {
  return Promise.resolve();
}

export function beforeNavigate(_callback: unknown) {}

export function afterNavigate(_callback: unknown) {}

export function onNavigate(_callback: unknown) {}

export function replaceState(_url: string, _state?: unknown) {}

export function pushState(_url: string, _state?: unknown) {}

export function preloadData(_url: string) {
  return Promise.resolve();
}

export function preloadCode(..._urls: string[]) {
  return Promise.resolve();
}
