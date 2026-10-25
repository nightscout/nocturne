/**
 * `$app/navigation` for a film. Navigation within the page's own route changes `page.url` in place,
 * so the page re-renders as the app would, and nothing ever leaves the composition. A film shows one
 * page, so a navigation elsewhere is ignored.
 */
import { page } from "./app-state.svelte.js";

function moveTo(url) {
  const next = new URL(url, page.url);
  if (next.pathname === page.url.pathname) page.url = next;
}

export function goto(url) {
  moveTo(url);
  return Promise.resolve();
}

export function replaceState(url, state) {
  if (url !== "") moveTo(url);
  page.state = state;
}

export function pushState(url, state) {
  replaceState(url, state);
}

export const invalidate = () => Promise.resolve();
export const invalidateAll = () => Promise.resolve();
export const preloadData = () => Promise.resolve();
export const preloadCode = () => Promise.resolve();
export const refreshAll = () => Promise.resolve();
export function beforeNavigate() {}
export function afterNavigate() {}
export function onNavigate() {}
export function disableScrollHandling() {}
