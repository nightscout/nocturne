/**
 * Stub for $app/navigation in browser test environment.
 */
import { page } from "./app-state";

export function goto(_url: string, _opts?: unknown) {
  return Promise.resolve();
}

export function invalidate(_url: string) {
  return Promise.resolve();
}

export function invalidateAll() {
  return Promise.resolve();
}

export function refreshAll() {
  return Promise.resolve();
}

export function beforeNavigate(_callback: unknown) {}

type AfterNavigateCallback = (navigation: { type: string }) => void;

/** Callbacks registered with `afterNavigate`. Nothing calls them unless a test does. */
export const afterNavigateCallbacks: AfterNavigateCallback[] = [];

export function afterNavigate(callback: AfterNavigateCallback) {
  afterNavigateCallbacks.push(callback);
}

export function onNavigate(_callback: unknown) {}

/** Shallow routing: the URL (when given) and `page.state` change, nothing navigates. */
function setShallowState(url: string | URL, state: App.PageState) {
  if (url !== "") page.url = new URL(url, page.url);
  page.state = state;
}

export function replaceState(url: string | URL, state: App.PageState) {
  setShallowState(url, state);
}

export function pushState(url: string | URL, state: App.PageState) {
  setShallowState(url, state);
}

export function preloadData(_url: string) {
  return Promise.resolve();
}

export function preloadCode(..._urls: string[]) {
  return Promise.resolve();
}
