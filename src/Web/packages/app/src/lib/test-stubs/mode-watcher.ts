/**
 * Stub for mode-watcher in node test environment.
 * mode-watcher only exports under the "svelte" condition which
 * is unavailable in vitest's node environment.
 */
export const mode = { current: "light", subscribe: () => () => {} };
export const userPrefersMode = { current: "system", subscribe: () => () => {} };
export function setMode() {}
export function toggleMode() {}
export function setInitialClassState() {}
export function resetMode() {}
export function derivedMode() { return mode; }
