/**
 * Stub for $app/navigation in browser test environment.
 */
export function goto(url: string, opts?: any) {
  return Promise.resolve();
}

export function invalidate(url: string) {
  return Promise.resolve();
}

export function invalidateAll() {
  return Promise.resolve();
}

export function beforeNavigate(callback: any) {}

export function afterNavigate(callback: any) {}

export function onNavigate(callback: any) {}

export function replaceState(url: string, state?: any) {}

export function pushState(url: string, state?: any) {}

export function preloadData(url: string) {
  return Promise.resolve();
}

export function preloadCode(...urls: string[]) {
  return Promise.resolve();
}
