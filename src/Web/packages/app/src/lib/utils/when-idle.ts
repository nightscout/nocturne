import { afterNextPaint } from "./after-next-paint";

/**
 * Runs `callback` once the browser is idle, or after `timeout` milliseconds at the latest. In
 * browsers without `requestIdleCallback` it runs after the next paint instead. Returns a cancel.
 */
export function whenIdle(
  callback: () => void,
  { timeout = 2000 }: { timeout?: number } = {},
): () => void {
  if (typeof requestIdleCallback !== "function") return afterNextPaint(callback);

  const handle = requestIdleCallback(() => callback(), { timeout });
  return () => cancelIdleCallback(handle);
}
